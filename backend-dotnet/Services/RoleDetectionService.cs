using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class RoleDetectionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    AiConcurrencyLimiter aiConcurrencyLimiter,
    ILogger<RoleDetectionService> logger)
{
    private const int MaxRoleSegmentsPerRequest = 50;

    private const string RoleClassificationPrompt = """
    You are a call-center transcript analyzer.

    Assign a speaker role to each numbered transcript segment.

    Segments may be English, Armenian, or mixed Armenian-English.
    Do not translate text. Only classify the speaker role for each segment id.
    Prefer the spoken intent over the previous visible label.
    Agent segments often ask verification or service questions, confirm actions, summarize a request, or say phrases like "I will add", "I will update", "I will create", "Would you like me", "Understood", or "Thank you for the call".
    Caller segments often provide personal facts, answer verification, make requests, correct details, or say phrases like "please", "can you", "I do not want", "yes please", or "everything is correct".
    Armenian Agent segments often include phrases like "շնորհակալություն զանգելու համար", "ինչպե՞ս կարող եմ օգնել", "ուրախ եմ օգնել", "կարո՞ղ եք հաստատել", or "ես կթարմացնեմ".
    Armenian Caller segments often include phrases like "իմ անունը", "ուզում եմ", "իմ հեռախոսահամար", "իհարկե", "ես ապրում եմ", or "իմ էլ".

    Allowed roles:
    - Agent
    - Caller
    - Unknown

    Return only valid minified JSON.
    Do not use markdown.
    Do not add explanations.
    Do not wrap JSON in ```json.
    Include one role object for every input segment id.

    Required JSON format:
    {"roles":[{"id":0,"role":"Agent"},{"id":1,"role":"Caller"}]}
    """;

    private const string RoleClassificationRetryPrompt = """
    Your previous role-classification response was not valid complete JSON.

    Return one complete minified JSON object only.
    Do not include transcript text, markdown, comments, or explanations.
    Include one role object for every input segment id.

    Required JSON format:
    {"roles":[{"id":0,"role":"Agent"},{"id":1,"role":"Caller"}]}
    """;

    public async Task<(IReadOnlyList<ConversationTurn> Turns, string Method, IReadOnlyList<string> Warnings)> DetectAsync(
        string transcript,
        IReadOnlyList<TranscriptChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (ContainsExplicitLabels(transcript))
        {
            var labeledTurns = ParseExplicitLabels(transcript);
            if (!configuration.AzureOpenAiConfigured)
            {
                var cueRefinedTurns = RefineTurnsWithEmbeddedCues(labeledTurns);
                return (
                    DeduplicateAdjacentTurns(cueRefinedTurns),
                    HasTurnChanges(labeledTurns, cueRefinedTurns) ? "labels+heuristic" : "labels",
                    []);
            }

            var (refinedTurns, refined) = await RefineExplicitLabelTurnsAsync(
                labeledTurns,
                cancellationToken);

            return (
                DeduplicateAdjacentTurns(refinedTurns),
                refined ? "labels+openai" : "labels",
                []);
        }

        if (configuration.AzureOpenAiConfigured)
        {
            var openAiTurns = new List<ConversationTurn>();
            var warnings = new List<string>();
            var results = await RunWithBoundedParallelismAsync(
                chunks,
                configuration.MaxParallelAiCalls,
                chunk => TryDetectChunkWithOpenAiAsync(chunk, cancellationToken),
                cancellationToken);

            foreach (var (chunk, result) in chunks.Zip(results))
            {
                var (chunkTurns, chunkWarning) = result;

                if (chunkTurns.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(chunkWarning))
                    {
                        warnings.Add(chunkWarning);
                    }

                    if (chunkTurns.Any(turn => turn.Role == "Unknown"))
                    {
                        warnings.Add($"Azure OpenAI role detection was uncertain for part of chunk {chunk.Index + 1}");
                    }

                    openAiTurns.AddRange(RefineTurnsWithEmbeddedCues(chunkTurns));
                    continue;
                }

                warnings.Add(chunkWarning ?? $"Azure OpenAI role detection was uncertain for chunk {chunk.Index + 1}; used Speaker fallback for that chunk");
                openAiTurns.AddRange(SpeakerFallback(chunk.Text));
            }

            if (openAiTurns.Count > 0)
            {
                return (
                    DeduplicateAdjacentTurns(openAiTurns),
                    warnings.Count == 0 ? "openai" : "openai-partial",
                    warnings
                );
            }
        }

        return (SpeakerFallback(transcript), "fallback", []);
    }

    private async Task<(IReadOnlyList<ConversationTurn> Turns, bool Refined)> RefineExplicitLabelTurnsAsync(
        IReadOnlyList<ConversationTurn> turns,
        CancellationToken cancellationToken)
    {
        var refinedTurns = new List<ConversationTurn>();
        var refined = false;

        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (!ShouldRefineLabeledTurn(turn))
            {
                refinedTurns.Add(turn);
                continue;
            }

            var (detectedTurns, warning) = await TryDetectChunkWithOpenAiAsync(
                new TranscriptChunk
                {
                    Index = i,
                    Start = 0,
                    End = turn.Text.Length,
                    Text = turn.Text
                },
                cancellationToken);

            if (detectedTurns.Count > 0 && string.IsNullOrWhiteSpace(warning))
            {
                var normalized = detectedTurns
                    .Select(detected => new ConversationTurn
                    {
                        Role = detected.Role == "Unknown" ? turn.Role : detected.Role,
                        Text = detected.Text
                    })
                    .ToList();

                var cueRefined = RefineTurnsWithEmbeddedCues(normalized);
                if (HasRoleSwitch(cueRefined))
                {
                    refinedTurns.AddRange(cueRefined);
                    refined = true;
                    continue;
                }
            }

            var heuristicRefined = RefineTurnWithEmbeddedCues(turn);
            if (HasRoleSwitch(heuristicRefined))
            {
                refinedTurns.AddRange(heuristicRefined);
                refined = true;
                continue;
            }

            refinedTurns.Add(turn);
        }

        return (MergeAdjacentSameRole(refinedTurns), refined);
    }

    private static async Task<TResult[]> RunWithBoundedParallelismAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maxParallelism,
        Func<TItem, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        var results = new TResult[items.Count];
        using var semaphore = new SemaphoreSlim(maxParallelism);

        var tasks = items.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = await work(item);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static bool ContainsExplicitLabels(string transcript)
    {
        return ExplicitLabelRegex().IsMatch(transcript);
    }

    private static bool ShouldRefineLabeledTurn(ConversationTurn turn)
    {
        if (turn.Text.Length >= 450)
        {
            return true;
        }

        var sentenceCount = SentenceRegex().Split(turn.Text)
            .Count(part => !string.IsNullOrWhiteSpace(part));

        return sentenceCount >= 5 || EmbeddedSpeakerCueRegex().IsMatch(turn.Text);
    }

    private static IReadOnlyList<ConversationTurn> RefineTurnsWithEmbeddedCues(IEnumerable<ConversationTurn> turns)
    {
        var refined = new List<ConversationTurn>();

        foreach (var turn in turns)
        {
            if (!ShouldRefineLabeledTurn(turn))
            {
                refined.Add(turn);
                continue;
            }

            var cueTurns = RefineTurnWithEmbeddedCues(turn);
            refined.AddRange(HasRoleSwitch(cueTurns) ? cueTurns : [turn]);
        }

        return MergeAdjacentSameRole(refined);
    }

    private static IReadOnlyList<ConversationTurn> RefineTurnWithEmbeddedCues(ConversationTurn turn)
    {
        var sentences = SplitRoleCandidate(turn.Text, forceSentenceSplit: true)
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        if (sentences.Count < 2)
        {
            return [turn];
        }

        var currentRole = turn.Role is "Agent" or "Caller" ? turn.Role : "Caller";
        string? previousSentence = null;
        var refined = new List<ConversationTurn>();

        foreach (var sentence in sentences)
        {
            var inferredRole = InferRoleFromCue(sentence, previousSentence, currentRole);
            var role = inferredRole ?? currentRole;

            AddOrMergeTurn(refined, role, sentence);

            currentRole = role;
            previousSentence = sentence;
        }

        return HasRoleSwitch(refined) ? refined : [turn];
    }

    private static string? InferRoleFromCue(string sentence, string? previousSentence, string currentRole)
    {
        var text = sentence.Trim();

        if (AgentCueRegex().IsMatch(text))
        {
            return "Agent";
        }

        if (CallerCueRegex().IsMatch(text))
        {
            return "Caller";
        }

        var normalized = text.Trim().TrimEnd('.', '!', '?', '։', ':');
        if (string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(previousSentence)
            && EndsWithQuestionOrPrompt(previousSentence))
        {
            return currentRole == "Agent" ? "Caller" : "Agent";
        }

        if (currentRole == "Agent"
            && !string.IsNullOrWhiteSpace(previousSentence)
            && EndsWithQuestionOrPrompt(previousSentence))
        {
            return "Caller";
        }

        return null;
    }

    private static bool EndsWithQuestionOrPrompt(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith("?", StringComparison.Ordinal)
            || trimmed.EndsWith("՞", StringComparison.Ordinal)
            || trimmed.EndsWith(":", StringComparison.Ordinal);
    }

    private static void AddOrMergeTurn(List<ConversationTurn> turns, string role, string text)
    {
        var previous = turns.LastOrDefault();
        if (previous is not null && previous.Role == role)
        {
            turns[^1] = new ConversationTurn
            {
                Role = previous.Role,
                Text = $"{previous.Text} {text}"
            };
            return;
        }

        turns.Add(new ConversationTurn
        {
            Role = role,
            Text = text
        });
    }

    private static bool HasRoleSwitch(IEnumerable<ConversationTurn> turns)
    {
        return turns
            .Select(turn => turn.Role)
            .Where(role => role is "Agent" or "Caller")
            .Distinct(StringComparer.Ordinal)
            .Count() > 1;
    }

    private static bool HasTurnChanges(IReadOnlyList<ConversationTurn> before, IReadOnlyList<ConversationTurn> after)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        return before.Zip(after).Any(pair =>
            !string.Equals(pair.First.Role, pair.Second.Role, StringComparison.Ordinal)
            || !string.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal));
    }

    private static IReadOnlyList<RoleSegment> BuildRoleSegments(string transcript)
    {
        var baseCandidates = SplitCandidateTurns(transcript).ToList();
        var shouldSplitSentences = baseCandidates.Count <= 1;
        var candidates = baseCandidates
            .SelectMany(text => SplitRoleCandidate(text, shouldSplitSentences))
            .Select(text => text.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(300)
            .ToList();

        return candidates
            .Select((text, index) => new RoleSegment(index, text))
            .ToList();
    }

    private static IEnumerable<string> SplitRoleCandidate(string text, bool forceSentenceSplit)
    {
        if (!forceSentenceSplit && text.Length <= 700)
        {
            return [text];
        }

        var normalized = EmbeddedBoundaryRegex().Replace(text, "\n");
        var sentences = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(part => SentenceRegex().Split(part))
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        return sentences.Count > 1 ? sentences : [text];
    }

    private static IReadOnlyList<ConversationTurn> ParseExplicitLabels(string transcript)
    {
        var turns = new List<ConversationTurn>();
        var matches = ExplicitLabelRegex().Matches(transcript);

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var role = NormalizeExplicitRole(match.Groups[1].Value);
            var textStart = match.Index + match.Length;
            var textEnd = i + 1 < matches.Count ? matches[i + 1].Index : transcript.Length;
            var text = transcript[textStart..textEnd].Trim();

            if (!string.IsNullOrWhiteSpace(text))
            {
                turns.Add(new ConversationTurn
                {
                    Role = role,
                    Text = text
                });
            }
        }

        return turns.Count > 0 ? turns : SpeakerFallback(transcript);
    }

    private async Task<(IReadOnlyList<ConversationTurn> Turns, string? Warning)> TryDetectChunkWithOpenAiAsync(
        TranscriptChunk chunk,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureOpenAiConfigured)
        {
            return ([], null);
        }

        try
        {
            var segments = BuildRoleSegments(chunk.Text);
            if (segments.Count == 0)
            {
                return ([], $"No role-detection segments found for chunk {chunk.Index + 1}");
            }

            var parsed = new List<ConversationTurn>();
            var warnings = new List<string>();

            foreach (var batch in segments.Chunk(MaxRoleSegmentsPerRequest))
            {
                var batchSegments = batch.ToList();
                var (batchTurns, batchWarning) = await TryDetectRoleBatchAsync(
                    chunk.Index,
                    batchSegments,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(batchWarning))
                {
                    warnings.Add(batchWarning);
                }

                parsed.AddRange(batchTurns.Count > 0
                    ? batchTurns
                    : RoleFallback(batchSegments, parsed.LastOrDefault()?.Role));
            }

            return (
                MergeAdjacentSameRole(parsed),
                warnings.Count > 0 ? string.Join("; ", warnings.Distinct(StringComparer.OrdinalIgnoreCase)) : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI role detection failed");

            return (
                [],
                $"Speaker split used fallback for chunk {chunk.Index + 1}"
            );
        }
    }

    private async Task<(IReadOnlyList<ConversationTurn> Turns, string? Warning)> TryDetectRoleBatchAsync(
        int chunkIndex,
        IReadOnlyList<RoleSegment> segments,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await SendRoleDetectionRequestAsync(
                RoleClassificationPrompt,
                segments,
                Math.Max(1000, Math.Min(3500, segments.Count * 70)),
                cancellationToken);

            if (!TryParseRoleDocument(content, out var roleDocument, out var parseError))
            {
                logger.LogWarning(
                    "Azure OpenAI role detection returned invalid JSON for chunk {Chunk}. Retrying. Parse error: {ParseError}",
                    chunkIndex + 1,
                    parseError);

                var retryContent = await SendRoleDetectionRequestAsync(
                    RoleClassificationRetryPrompt,
                    segments,
                    Math.Max(1500, Math.Min(4500, segments.Count * 90)),
                    cancellationToken);

                if (!TryParseRoleDocument(retryContent, out roleDocument, out parseError))
                {
                    logger.LogWarning(
                        "Azure OpenAI role detection retry returned invalid JSON for chunk {Chunk}. Parse error: {ParseError}",
                        chunkIndex + 1,
                        parseError);

                    return (
                        [],
                        $"Speaker split used fallback for part of chunk {chunkIndex + 1}");
                }
            }

            using (roleDocument)
            {
                if (!roleDocument.RootElement.TryGetProperty("roles", out var rolesElement)
                    || rolesElement.ValueKind != JsonValueKind.Array)
                {
                    return (
                        [],
                        $"Speaker split used fallback for part of chunk {chunkIndex + 1}"
                    );
                }

                var rolesById = new Dictionary<int, string>();

                foreach (var item in rolesElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement)
                        || !idElement.TryGetInt32(out var id))
                    {
                        continue;
                    }

                    var role = item.TryGetProperty("role", out var roleElement)
                        ? roleElement.GetString()
                        : "Unknown";

                    rolesById[id] = role is "Agent" or "Caller" ? role : "Unknown";
                }

                var parsed = segments
                    .Select(segment => new ConversationTurn
                    {
                        Role = rolesById.TryGetValue(segment.Id, out var role) ? role : "Unknown",
                        Text = segment.Text
                    })
                    .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
                    .ToList();

                return (MergeAdjacentSameRole(parsed), null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI role detection failed");

            return (
                [],
                $"Speaker split used fallback for part of chunk {chunkIndex + 1}"
            );
        }
    }

    private async Task<string> SendRoleDetectionRequestAsync(
        string prompt,
        IReadOnlyList<RoleSegment> segments,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var endpoint = configuration.AzureOpenAiEndpoint.TrimEnd('/');
        var url = $"{endpoint}/responses";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await ApplyAuthorizationAsync(request, cancellationToken);

        request.Content = JsonContent(new
        {
            model = configuration.AzureOpenAiDeployment,
            input = prompt + "\n\nSegments JSON:\n" + JsonSerializer.Serialize(segments),
            text = new
            {
                format = new
                {
                    type = "json_object"
                }
            },
            max_output_tokens = maxOutputTokens
        });

        var client = httpClientFactory.CreateClient();

        using var response = await aiConcurrencyLimiter.RunAsync(
            "Azure OpenAI role detection",
            token => client.SendAsync(request, token),
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Azure OpenAI role detection failed with status {StatusCode}. Response: {ResponseText}",
                response.StatusCode,
                responseText);

            throw new InvalidOperationException($"Azure OpenAI role detection failed with status {(int)response.StatusCode}");
        }

        var content = ExtractTextFromResponsesApi(responseText);
        logger.LogInformation("Azure OpenAI role detection content: {Content}", content);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Azure OpenAI role detection returned empty content");
        }

        return content;
    }

    private static bool TryParseRoleDocument(
        string content,
        out JsonDocument roleDocument,
        out string? parseError)
    {
        try
        {
            roleDocument = JsonDocument.Parse(content);
            parseError = null;
            return true;
        }
        catch (JsonException ex)
        {
            roleDocument = JsonDocument.Parse("{}");
            parseError = ex.Message;
            return false;
        }
    }

    private static string? ExtractTextFromResponsesApi(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        // Some Responses API responses include output_text directly.
        if (root.TryGetProperty("output_text", out var outputTextElement)
            && outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString();
        }

        /*
         Standard Responses API shape:
         {
           "output": [
             {
               "content": [
                 {
                   "type": "output_text",
                   "text": "..."
                 }
               ]
             }
           ]
         }
        */
        if (root.TryGetProperty("output", out var outputElement)
            && outputElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentElement.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        return textElement.GetString();
                    }
                }
            }
        }

        // Fallback for old Chat Completions shape.
        if (root.TryGetProperty("choices", out var choicesElement)
            && choicesElement.ValueKind == JsonValueKind.Array
            && choicesElement.GetArrayLength() > 0)
        {
            var firstChoice = choicesElement[0];

            if (firstChoice.TryGetProperty("message", out var messageElement)
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString();
            }
        }

        return null;
    }

    private async Task ApplyAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldUseApiKeyAuth()
            && !string.IsNullOrWhiteSpace(configuration.AzureOpenAiKey))
        {
            request.Headers.Add("api-key", configuration.AzureOpenAiKey);
            return;
        }

        /*
         Azure AI Foundry endpoints can use DefaultAzureCredential.
         Before running locally with bearer auth, run: az login
        */
        var credential = new DefaultAzureCredential();

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]),
            cancellationToken
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private bool ShouldUseApiKeyAuth()
    {
        var endpoint = configuration.AzureOpenAiEndpoint.Trim().ToLowerInvariant();
        return endpoint.Contains(".openai.azure.com", StringComparison.Ordinal);
    }

    private static IReadOnlyList<ConversationTurn> SpeakerFallback(string transcript)
    {
        var parts = SplitCandidateTurns(transcript).ToList();

        if (parts.Count == 0)
        {
            parts = SentenceRegex().Split(transcript)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            parts.Add(transcript.Trim());
        }

        var firstRole = InferFallbackStartRole(parts.FirstOrDefault() ?? string.Empty);
        var secondRole = firstRole == "Agent" ? "Caller" : "Agent";

        return parts.Select((text, index) => new ConversationTurn
        {
            Role = index % 2 == 0 ? firstRole : secondRole,
            Text = text
        }).ToList();
    }

    private static string InferFallbackStartRole(string text)
    {
        if (AgentCueRegex().IsMatch(text)
            || Regex.IsMatch(text, @"\b(how can i help|thank you for calling|this is .+ speaking)\b", RegexOptions.IgnoreCase))
        {
            return "Agent";
        }

        if (CallerCueRegex().IsMatch(text)
            || Regex.IsMatch(text, @"\b(i'?m calling|i am calling|my name is|i would like|i need|i want|my phone|my email|my address|issues? still)\b", RegexOptions.IgnoreCase))
        {
            return "Caller";
        }

        return "Caller";
    }

    private static IReadOnlyList<ConversationTurn> RoleFallback(
        IReadOnlyList<RoleSegment> segments,
        string? previousRole)
    {
        var turns = new List<ConversationTurn>();
        var currentRole = previousRole is "Agent" or "Caller" ? previousRole : "Caller";
        string? previousText = null;

        foreach (var segment in segments)
        {
            var inferredRole = InferRoleFromCue(segment.Text, previousText, currentRole);
            var role = inferredRole ?? currentRole;

            AddOrMergeTurn(turns, role, segment.Text);
            currentRole = role;
            previousText = segment.Text;
        }

        return turns;
    }

    private static IReadOnlyList<ConversationTurn> DeduplicateAdjacentTurns(IEnumerable<ConversationTurn> turns)
    {
        var deduped = new List<ConversationTurn>();

        foreach (var turn in turns)
        {
            var previous = deduped.LastOrDefault();

            if (previous is not null
                && previous.Role == turn.Role
                && string.Equals(previous.Text, turn.Text, StringComparison.Ordinal))
            {
                continue;
            }

            deduped.Add(turn);
        }

        return deduped;
    }

    private static IReadOnlyList<ConversationTurn> MergeAdjacentSameRole(IEnumerable<ConversationTurn> turns)
    {
        var merged = new List<ConversationTurn>();

        foreach (var turn in turns)
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && previous.Role == turn.Role)
            {
                merged[^1] = new ConversationTurn
                {
                    Role = previous.Role,
                    Text = $"{previous.Text}\n\n{turn.Text}"
                };
                continue;
            }

            merged.Add(new ConversationTurn
            {
                Role = turn.Role,
                Text = turn.Text
            });
        }

        return merged;
    }

    private static IEnumerable<string> SplitCandidateTurns(string transcript)
    {
        var lines = transcript
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count > 1)
        {
            return lines;
        }

        return transcript
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(block => !string.IsNullOrWhiteSpace(block));
    }

    private static string NormalizeExplicitRole(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();

        return normalized switch
        {
            "agent" or "representative" or "rep" or "support" or "operator" or "assistant"
                or "advisor" or "specialist" or "nurse" or "doctor" or "clinic" or "staff"
                or "speaker 1" or "speaker1" => "Agent",
            "գործակալ" or "օպերատոր" => "Agent",

            "caller" or "customer" or "client" or "patient" or "member" or "user"
                or "speaker 2" or "speaker2" => "Caller",
            "զանգահարող" or "հաճախորդ" => "Caller",

            _ => "Speaker 1"
        };
    }

    private static StringContent JsonContent(object value)
    {
        return new StringContent(
            JsonSerializer.Serialize(value),
            Encoding.UTF8,
            "application/json"
        );
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(agent|caller|customer|client|patient|representative|rep|support|operator|assistant|advisor|specialist|nurse|doctor|clinic|staff|member|user|speaker\s*[12]|Գործակալ|Զանգահարող|Հաճախորդ|Օպերատոր)\s*[:\-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitLabelRegex();

    [GeneratedRegex(@"(?<!էլ\.)(?<!բնակ\.)(?<=[.!?։:])\s+")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"\s+(?=(ձեր էլ\.?\s*հասցեն|ձեր հասցեն|շնորհակալություն,|կարո՞ղ եք|կարող եք|շատ լավ|մի պահ սպասեք|լավ,\s*շնորհակալություն|your email address|your address|can you also|could you also|thank you[, ]|one moment|please hold))", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedBoundaryRegex();

    [GeneratedRegex(@"\b(can you confirm|please confirm|i will update|i will add|i can create|i can help|i will note|i will link|i will create|would you like me|understood|thank you[,.]?\s*i will|yes,\s*i can|yes,\s*that is important|okay,\s*i will|please also|good\.)\b|ձեր էլ\.?\s*հասցեն|ձեր հասցեն|կարո՞ղ եք նաև նշել|շատ լավ|մի պահ սպասեք|հիմա կստուգեմ|^հասցեն:?$", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedSpeakerCueRegex();

    [GeneratedRegex(@"^(for verification,\s*)?(can you confirm|could you confirm|please confirm)\b|^(i'll|i will|i can|i am going to|i'm going to)\s+(add|update|note|create|link|send|request|include|submit|open|escalate|attach|ask|check|make|mark|email|process|review|verify|confirm)\b|^(okay|ok|understood|thank you)[,.\s]+(i'll|i will|i can)\b|^yes[,.]?\s+(that is important|i can|i will)\b|^would you like me\b|^thank you for the call\b|^have a good day\b|շնորհակալություն զանգելու համար|ինչպե՞ս կարող եմ օգնել|ուրախ եմ օգնել|կարո՞ղ եք հաստատել|կարող եք հաստատել|ձեր էլ\.?\s*հասցեն|^հասցեն:?$|ձեր հասցեն|^շնորհակալություն,\s*[\p{L}\s.-]{1,40}[։.!:]?$|կարո՞ղ եք նաև նշել|շատ լավ|մի պահ սպասեք|հիմա կստուգեմ|ես կթարմացնեմ|կթարմացնեմ", RegexOptions.IgnoreCase)]
    private static partial Regex AgentCueRegex();

    [GeneratedRegex(@"^(please|also please)\b|^(can you|could you)\b|^(sure|yes[,.]?\s+(please|everything|the|my|i|it|there|sure))\b|^(i do not|i don't|i want|i need)\b|^good[.!]?$|^there are also old ticket\b|^ticket\s+[A-Z]+[-\d]\b|^thank you,\s*i\b|իմ անունը|ուզում եմ|իմ հեռախոսահամար|իհարկե|ես ապրում եմ|իմ էլ|բարեւ|^[\w.%+-]+@[\w.-]+\.[A-Z]{2,}\b|^[A-Z]{2}\d{7}\b|^լավ,\s*շնորհակալություն", RegexOptions.IgnoreCase)]
    private static partial Regex CallerCueRegex();

    private sealed record RoleSegment(int Id, string Text);
}

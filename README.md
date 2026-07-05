# Azure AI Transcript Analyzer

A local web app that extracts structured PII attributes (name, address, phone, email, SSN/ID, etc.) from call-center transcripts in **English** and **Armenian**, using Azure AI Language for NLP and regex as a fallback.

---

## What the app does

1. You paste a raw transcript into the UI.
2. The backend splits it into conversation turns and assigns speaker roles (Agent / Caller) using heuristic rules.
3. Azure AI Language's PII recognition extracts named entities.
4. Regex patterns fill any gaps Azure doesn't cover.
5. The UI shows extracted attributes in a table, the conversation, Azure raw entities, and a full JSON view.

---

## Creating an Azure AI Language resource

1. Go to [https://portal.azure.com](https://portal.azure.com) and sign in.
2. Click **Create a resource** → search for **Language service**.
3. Select **Language service** → **Create**.
4. Fill in:
   - **Subscription** — your subscription
   - **Resource group** — create or pick one
   - **Region** — e.g. East US
   - **Name** — e.g. `my-language-resource`
   - **Pricing tier** — Free (F0) works for testing
5. Click **Review + Create** → **Create**.
6. Once deployed, go to the resource → **Keys and Endpoint**.
7. Copy **Key 1** and the **Endpoint** URL.

---

## Configuration

```bash
cd backend
cp .env.example .env
```

Edit `.env`:

```env
AZURE_LANGUAGE_ENDPOINT=https://my-language-resource.cognitiveservices.azure.com/
AZURE_LANGUAGE_KEY=abc123...your32charkey...
```

> If you leave these empty the app still runs — it falls back to regex extraction and shows a warning banner in the UI.

---

## Running the backend

```bash
# From the project root:
cd azure-transcript-analyzer/backend

# Create a virtual environment
python3 -m venv venv
source venv/bin/activate        # Windows: venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt

# Copy and configure environment (add your Azure keys if you have them)
cp .env.example .env

# Start the server
uvicorn main:app --reload --port 8000
```

The API is now available at `http://localhost:8000`.  
Open `http://localhost:8000/docs` for the interactive Swagger UI.

---

## Running the frontend

```bash
# From the project root (in a second terminal):
cd azure-transcript-analyzer/frontend

# Install Node dependencies
npm install

# Start the dev server
npm run dev
```

Open `http://localhost:5173` in your browser.

---

## Sample transcripts

### English

```
Agent: Thank you for calling support. How can I help you today?
Caller: Hi, my name is John Smith. I'm having trouble with my account.
Agent: I'd be happy to help. Can you please confirm your phone number for me?
Caller: Sure, my phone number is 555-867-5309.
Agent: And your email address on the account?
Caller: It's john.smith@example.com
Agent: Can you also confirm your address?
Caller: I live at 742 Evergreen Terrace, Springfield, IL 62701.
Agent: Thank you John. One last thing — can you provide the last four digits of your Social Security Number?
Caller: Yes, my SSN is 123-45-6789.
```

### Armenian

```
Գործակալ: Բարև ձեզ, շնորհակալություն զանգելու համար։ Ինչպե՞ս կարող եմ օգնել ձեզ:
Զանգահարող: Բարև, իմ անունը Արա Պետրոսյան է։ Ուզում եմ հարցնել իմ հաշվի մասին։
Գործակալ: Ուրախ եմ օգնել։ Կարո՞ղ եք հաստատել ձեր հեռախոսահամարը:
Զանգահարող: Իհարկե, իմ հեռախոսահամարն է +374 91 123456:
Գործակալ: Ձեր էլ. հասցե՞ն:
Զանգահարող: ara.petrosyan@mail.am
Գործակալ: Ձեր հասցե՞ն:
Զանգահարող: Ես ապրում եմ Երևան, Աբովյան փողոց 15, բնակ. 3:
```

---

## API reference

### `GET /health`

```json
{ "status": "ok", "azure_configured": true }
```

### `POST /analyze`

**Request:**
```json
{
  "language": "en",
  "transcript": "Agent: How can I help?\nCaller: My name is John..."
}
```

**Response:**
```json
{
  "conversation": [
    { "role": "Agent", "text": "How can I help?" },
    { "role": "Caller", "text": "My name is John..." }
  ],
  "extractedAttributes": {
    "name": "John",
    "address": "",
    "socialSecurityNumber": "",
    "phoneNumber": "",
    "email": "",
    "other": []
  },
  "rawAzureEntities": [],
  "warning": null
}
```

---

## Limitations

- **Armenian dialect** — Azure AI Language has limited NER support for Armenian. Regex patterns cover phone numbers and passport-style IDs but named-entity extraction for Armenian names and addresses relies on Azure's model quality.
- **Speaker role detection** — roles are assigned by heuristic keyword matching. If the transcript has no explicit `Agent:` / `Caller:` prefixes, classification may be incorrect for ambiguous lines.
- **SSN** — the US SSN pattern (`NNN-NN-NNNN`) is specific to the US. Armenian national ID is matched as a separate regex (`AM1234567`-style passport numbers).
- **Address extraction** — addresses are extracted by regex lookbehind patterns ("I live at…"). Complex or unstructured addresses may not be captured correctly.

---

## Recommended future improvements

- **Azure OpenAI Structured Outputs** — replace heuristic role detection with a prompt that enforces the `ConversationTurn` JSON schema, yielding much more accurate Agent/Caller classification and better Armenian name/address extraction.
- **Custom named-entity model** — train an Azure Custom NER model on Armenian call-center data for domain-specific entity types.
- **Speaker diarization** — integrate Azure Speech Service (if audio is available) for true speaker separation before transcription.
- **Rate limiting & auth** — add an API key or OAuth layer before deploying beyond localhost.

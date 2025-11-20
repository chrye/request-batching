# Azure OpenAI GPT-4 .NET Example

This project demonstrates how to call Azure OpenAI GPT-4 model from .NET using the REST API.

It showcases different ways of batching API calls to LLM, to best utilizing the TPM & RPM limits yet avoiding 429 errors.

## Prerequisites

- .NET 8.0 SDK or later
- Azure OpenAI resource with a GPT-4 deployment

## Setup

### 1. Get Your Azure OpenAI Credentials

You need three pieces of information from your Azure OpenAI resource:

1. **Endpoint**: Found in Azure Portal under your Azure OpenAI resource → "Keys and Endpoint"
   - Example: `https://your-resource.openai.azure.com/`

2. **API Key**: Found in the same location
   - Example: `abc123def456...`

3. **Deployment Name**: The name you gave your GPT-4 deployment
   - Example: `gpt-4`, `gpt-4-turbo`, or your custom name

### 2. Configure appsettings.json

1. The project includes an `appsettings.json` file. Open it and replace the placeholder values with your actual credentials:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiKey": "your-api-key-here",
    "DeploymentName": "gpt-4"
  }
}
```

2. **Important**: The `appsettings.json` file is excluded from git (via `.gitignore`) to protect your API keys. An `appsettings.example.json` file is provided as a template.

## Build and Run

1. **Restore NuGet packages:**
   ```powershell
   dotnet restore
   ```

2. **Build the project:**
   ```powershell
   dotnet build
   ```

3. **Run the application:**
   ```powershell
   dotnet run
   ```

## Project Structure

- **AzureOpenAIClient.cs** - Main client class for interacting with Azure OpenAI
  - `SendPromptAsync()` - Send a single prompt to the model
  - `SendConversationAsync()` - Send multi-turn conversations

- **Program.cs** - Example usage with three scenarios:
  1. Simple prompt
  2. Tax-related question
  3. Multi-turn conversation

## Usage Examples

### Basic Prompt

```csharp
var client = new AzureOpenAIClient(endpoint, apiKey, deploymentName);

var response = await client.SendPromptAsync(
    prompt: "What is Azure OpenAI?",
    systemMessage: "You are a helpful AI assistant.",
    temperature: 0.7,
    maxTokens: 800
);

Console.WriteLine(response);
```

### Multi-turn Conversation

```csharp
var messages = new Message[]
{
    new Message { role = "system", content = "You are a helpful assistant." },
    new Message { role = "user", content = "Tell me about Paris" },
    new Message { role = "assistant", content = "Paris is the capital of France..." },
    new Message { role = "user", content = "What's the weather like there?" }
};

var response = await client.SendConversationAsync(messages);
```

## Configuration Parameters

- **temperature** (0.0 - 2.0): Controls randomness. Lower values are more deterministic.
- **maxTokens**: Maximum number of tokens in the response
- **systemMessage**: Sets the behavior and context for the AI assistant

## Notes

- This implementation uses the Azure OpenAI REST API directly (no SDK dependencies)
- Uses the `2024-08-01-preview` API version for Chat Completions
- Includes proper error handling and resource disposal
- Supports both GPT-4 and GPT-4 Turbo deployments

## Security Best Practices

- ✅ **Never commit API keys to source control** - The `.gitignore` file excludes `appsettings.json`
- ✅ **Use the example file** - Copy `appsettings.example.json` to `appsettings.json` and add your credentials
- Consider using Azure Key Vault for production credentials
- Consider using Azure Managed Identity in production environments
- Implement rate limiting and retry logic for production apps

## Troubleshooting

**401 Unauthorized**: Check your API key and ensure it's correct

**404 Not Found**: Verify your endpoint URL and deployment name

**429 Too Many Requests**: You've exceeded your quota. Implement rate limiting or request a quota increase

**Model not found**: Ensure your deployment name matches exactly what's in Azure Portal

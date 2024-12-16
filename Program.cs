using Azure.CloudMachine.AppService;
using Azure.CloudMachine.OpenAI;
using Azure.CloudMachine;
using OpenAI.Chat;

CloudMachineInfrastructure infrastrucutre = new();
infrastrucutre.AddFeature(new OpenAIModelFeature("gpt-35-turbo", "0125"));
infrastrucutre.AddFeature(new OpenAIModelFeature("text-embedding-ada-002", "2", AIModelKind.Embedding));
infrastrucutre.AddFeature(new AppServiceFeature());
CloudMachineClient client = infrastrucutre.GetClient();

// the app can be called with -init switch to generate bicep and prepare for azd deployment.
if (infrastrucutre.TryExecuteCommand(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

// Add CloudMachine to the DI container
builder.Services.AddSingleton(client);

var app = builder.Build();
app.MapRazorPages();
app.UseStaticFiles();

EmbeddingsVectorbase _vectorDb = new(client.GetOpenAIEmbeddingsClient());
List<ChatMessage> _prompt = [];

// Register the vector db to be updated when a new file is uploaded
client.Storage.WhenUploaded(_vectorDb.Add);

app.MapPost("/upload", async (HttpRequest request)
    => await client.Storage.UploadFormAsync(request));

app.MapPost("/chat", async (HttpRequest request) =>
{
    try
    {
        var message = await new StreamReader(request.Body).ReadToEndAsync();
        IEnumerable<VectorbaseEntry> related = _vectorDb.Find(message);
        _prompt.Add(related);
        _prompt.Add(ChatMessage.CreateUserMessage(message));

        ChatClient chat = client.GetOpenAIChatClient();
        ChatCompletion completion = await chat.CompleteChatAsync(_prompt);
        switch (completion.FinishReason)
        {
            case ChatFinishReason.Stop:
                _prompt.Add(completion);
                string response = completion.Content[0].Text;
                return response;
            #region NotImplemented
            //case ChatFinishReason.Length:
            //case ChatFinishReason.ToolCalls:
            //case ChatFinishReason.ContentFilter:
            //case ChatFinishReason.FunctionCall:
            default:
                return ($"FinishReason {completion.FinishReason} not implemented");
                #endregion
        }
    }
    catch (Exception ex)
    {
        return ex.Message;
    }
});

app.Run();
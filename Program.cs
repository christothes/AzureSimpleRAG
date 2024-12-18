using Azure.CloudMachine.AppService;
using Azure.CloudMachine.OpenAI;
using Azure.CloudMachine;
using OpenAI.Chat;
using System.Net.WebSockets;
using System.IO;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.RealtimeConversation;
using AzureSimpleRAG;
using Azure.Core;

CloudMachineInfrastructure infrastrucutre = new();
infrastrucutre.AddFeature(new OpenAIModelFeature("gpt-4o-realtime-preview", "2024-10-01"));
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
app.UseWebSockets();

EmbeddingsVectorbase _vectorDb = new(client.GetOpenAIEmbeddingsClient());
List<ChatMessage> _prompt = [];

var clientId = Environment.GetEnvironmentVariable("CLOUDMACHINE_MANAGED_IDENTITY_CLIENT_ID");
TokenCredential cred = clientId == null ? new AzureCliCredential() : new ManagedIdentityCredential(clientId);
AzureOpenAIClient aoaiClient = new(
    new Uri("https://cm0ddf918b146443b.openai.azure.com/openai/realtime?api-version=2024-10-01-preview&deployment=cm0ddf918b146443b_chat"), 
    cred);
RealtimeConversationClient? realtime = aoaiClient.GetRealtimeConversationClient("cm0ddf918b146443b_chat");

// We'll add a simple function tool that enables the model to interpret user input to figure out when it
// might be a good time to stop the interaction.
ConversationFunctionTool finishConversationTool = new()
{
    Name = "user_wants_to_finish_conversation",
    Description = "Invoked when the user says goodbye, expresses being finished, or otherwise seems to want to stop the interaction.",
    Parameters = BinaryData.FromString("{}")
};

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

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/audio")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine("Echo called");
            await Echo(webSocket);
            Console.WriteLine("Echo exited");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }

});

app.Run();


async Task<RealtimeConversationSession> InitSession(RealtimeConversationClient rtClient)
{
    RealtimeConversationSession session = await rtClient.StartConversationSessionAsync();

    // Now we configure the session using the tool we created along with transcription options that enable input
    // audio transcription with whisper.
    await session.ConfigureSessionAsync(new ConversationSessionOptions()
    {
        Tools = { finishConversationTool },
        InputTranscriptionOptions = new()
        {
            Model = "whisper-1",
        },
        TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                               detectionThreshold: 0.42f,
                               //prefixPaddingDuration: TimeSpan.FromMilliseconds(234),
                               silenceDuration: TimeSpan.FromMilliseconds(1000))
    });
    return session;
}

async Task Echo(WebSocket webSocket)
{
    using RealtimeConversationSession session = await InitSession(realtime);
    // With the session configured, we start processing commands received from the service.
    await foreach (ConversationUpdate update in session.ReceiveUpdatesAsync())
    {
        // session.created is the very first command on a session and lets us know that connection was successful.
        if (update is ConversationSessionStartedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($" <<< Connected: session started");
            // This is a good time to start capturing microphone input and sending audio to the service. The
            // input stream will be chunked and sent asynchronously, so we don't need to await anything in the
            // processing loop.
            _ = Task.Run(async () =>
            {
                using var stream = WebSocketAudioStream.Start(webSocket);
                Console.WriteLine($" >>> Listening to microphone input");
                Console.WriteLine($" >>> (Just tell the app you're done to finish)");
                Console.WriteLine();
                await session.SendInputAudioAsync(stream);
                Console.WriteLine("SendInputAudioAsync exited");
            });
        }

        // input_audio_buffer.speech_started tells us that the beginning of speech was detected in the input audio
        // we're sending from the microphone.
        if (update is ConversationInputSpeechStartedUpdate speechStartedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($" <<< Start of speech detected @ {speechStartedUpdate.AudioStartTime}");
            // Like any good listener, we can use the cue that the user started speaking as a hint that the app
            // should stop talking. Note that we could also track the playback position and truncate the response
            // item so that the model doesn't "remember things it didn't say" -- that's not demonstrated here.
            //speakerOutput.ClearPlayback();
            await webSocket.SendAsync(new byte[] { 0x00, 0x00, 0x00, 0x00 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        // input_audio_buffer.speech_stopped tells us that the end of speech was detected in the input audio sent
        // from the microphone. It'll automatically tell the model to start generating a response to reply back.
        if (update is ConversationInputSpeechFinishedUpdate speechFinishedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($" <<< End of speech detected @ {speechFinishedUpdate.AudioEndTime}");
        }

        // conversation.item.input_audio_transcription.completed will only arrive if input transcription was
        // configured for the session. It provides a written representation of what the user said, which can
        // provide good feedback about what the model will use to respond.
        if (update is ConversationInputTranscriptionFinishedUpdate transcriptionFinishedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine($" >>> USER: {transcriptionFinishedUpdate.Transcript}");
        }

        // Item streaming delta updates provide a combined view into incremental item data including output
        // the audio response transcript, function arguments, and audio data.
        if (update is ConversationItemStreamingPartDeltaUpdate deltaUpdate)
        {
            Console.Write(deltaUpdate.AudioTranscript);
            Console.Write(deltaUpdate.Text);
            //speakerOutput.EnqueueForPlayback(deltaUpdate.AudioBytes);
            await webSocket.SendAsync(deltaUpdate.AudioBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        // response.output_item.done tells us that a model-generated item with streaming content is completed.
        // That's a good signal to provide a visual break and perform final evaluation of tool calls.
        if (update is ConversationItemStreamingFinishedUpdate itemFinishedUpdate)
        {
            Console.WriteLine();
            Console.WriteLine("=====================");
            if (itemFinishedUpdate.FunctionName == finishConversationTool.Name)
            {
                Console.WriteLine($" <<< Finish tool invoked -- ending conversation!");
                break;
            }
        }

        // error commands, as the name implies, are raised when something goes wrong.
        if (update is ConversationErrorUpdate errorUpdate)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($" <<< ERROR: {errorUpdate.Message}");
            Console.WriteLine(errorUpdate.GetRawContent().ToString());
            break;
        }
    }
    if (webSocket.State == WebSocketState.Open)
    {
        Console.WriteLine("Closing WebSocket");
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }

}
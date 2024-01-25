using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;

//load config file
IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
Settings settings = config.GetRequiredSection("Settings").Get<Settings>()!;

Console.WriteLine($"Starting {settings.Personality.Name}...");
Console.Title = settings.Personality.Name;

//initialize OpenAI
Model model = new(settings.Connections.OpenAIAPI.Model, "openai");
OpenAIClient api = new OpenAIClient(settings.Connections.OpenAIAPI.Token);

//call with just the system prompt to determine prompt token cost
ChatRequest chatRequest = new ChatRequest(new List<OpenAI.Chat.Message> { new OpenAI.Chat.Message(Role.System, settings.Personality.Prompt) }, model: model, maxTokens: settings.Connections.OpenAIAPI.TokensToKeep);
ChatResponse result = await api.ChatEndpoint.GetCompletionAsync(chatRequest);

var prompts = new List<(OpenAI.Role role, dynamic content, DateTime stamp, int tokens)>
{
    (Role.System, settings.Personality.Prompt, DateTime.UtcNow, (int)result.Usage.PromptTokens!)
};

//initialize Telegram
TelegramBotClient botClient = new TelegramBotClient(settings.Connections.TelegramAPI.Token);
User me = await botClient.GetMeAsync();

if (settings.Connections.TelegramAPI.Username.Equals($"@{me.Username}", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Connected to Telegram as {settings.Connections.TelegramAPI.Username} with ID {me.Id}.");
}
else
{
    Console.WriteLine($"Error: Connected to Telegram as @{me.Username} with ID {me.Id}, but configured name is {settings.Connections.TelegramAPI.Username}!");
    return;
}
using var cts = new CancellationTokenSource();
Telegram.Bot.Polling.ReceiverOptions receiverOptions = new() { AllowedUpdates = new[] { UpdateType.Message, UpdateType.ChatMember } };
botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, cts.Token);

//loop
while (Console.ReadKey().Key != ConsoleKey.Escape) { }
cts.Cancel();

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    var ErrorMessage = exception switch
    {
        ApiRequestException apiRequestException => $"[Error {apiRequestException.ErrorCode}] {apiRequestException.Message}",
        _ => exception.ToString()
    };

    Console.WriteLine(ErrorMessage);
    return Task.CompletedTask;
}

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    try
    {
        var msg = update.Message ?? update.EditedMessage ?? update.ChannelPost ?? update.EditedChannelPost;
        if (update.EditedMessage != null) update.Message = update.EditedMessage;
        if (!(update.Message?.Type == MessageType.Text || (settings.Connections.OpenAIAPI.VisionSupport && update.Message?.Type == MessageType.Photo))) return;

        // check if allowed chat
        if (settings.Connections.TelegramAPI.AllowedChats != null && settings.Connections.TelegramAPI.AllowedChats.Count() > 0 && !settings.Connections.TelegramAPI.AllowedChats.Contains(update.Message!.Chat.Id)) return;
        if (update.Message!.Chat.Type == ChatType.Private && !settings.Connections.TelegramAPI.AllowPrivateMessages) return;
        long chatId = update.Message!.Chat.Id;
        string prompt = "";
        if (update.Message!.Type == MessageType.Text)
        {
            prompt = update.Message!.Text!;
        }
        else
        {
            prompt = update.Message!.Caption!;
        }

        if (prompt.StartsWith(settings.Connections.TelegramAPI.Username, StringComparison.OrdinalIgnoreCase))
        {
            //new talk
            prompt = prompt.Replace(settings.Connections.TelegramAPI.Username, "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        else if (update.Message.ReplyToMessage?.From?.Id == me.Id)
        {
            //reply
        }
        else if (update.Message.Chat.Type == ChatType.Private)
        {
            //private messages don't need namecalling
        }
        else if (!settings.Personality.RespondToName && update.Message.ReplyToMessage?.From?.Id != me.Id && !prompt.StartsWith(settings.Connections.TelegramAPI.Username, StringComparison.OrdinalIgnoreCase) || !prompt.Contains(settings.Personality.Name, StringComparison.OrdinalIgnoreCase) && update.Message.ReplyToMessage?.From?.Id != me.Id)
        {
            //ignore this message
            return;
        }

        if (prompt.Length > settings.Connections.TelegramAPI.MessageLengthLimit)
        {
            Telegram.Bot.Types.Message sentMessage = await botClient.SendTextMessageAsync(
                               chatId: chatId,
                               text: "Sorry but this message is too long for me to parse.",
                               cancellationToken: cts.Token,
                               replyToMessageId: update.Message.MessageId);

            Console.WriteLine("A: Sorry but this message is too long for me to parse.");
            return;
        }

        Console.WriteLine($"< {update.Message.From?.FirstName}: {prompt}");

        //we're going to answer! Send a typing indicator...
        await bot.SendChatActionAsync(update.Message.Chat.Id, ChatAction.Typing, null, ct);

        // clean up to old messages
        while (prompts.Count > 1 && prompts[1].stamp < (DateTime.UtcNow).AddMinutes(settings.Connections.OpenAIAPI.MinutesToKeep * -1)) prompts.RemoveAt(1);

        // clean up messages when over token limit
        while (prompts.Sum(p => p.tokens) > settings.Connections.OpenAIAPI.TokensToKeep) prompts.RemoveAt(1);

        if (prompt.StartsWith("/status"))
        {
            TimeSpan bootTime = DateTime.UtcNow.Subtract(prompts[0].stamp);
            var tokensInMemory = prompts.Count > 1 ? (prompts.Sum(p => p.tokens) - prompts[0].tokens) : 0;
            var messagesInMemory = prompts.Count - 1;
            TimeSpan oldestMessageTime = prompts.Count > 1 ? DateTime.UtcNow.Subtract(prompts[1].stamp) : TimeSpan.Zero;

            string statusMessage = $"ℹ️ This is <b>{settings.Personality.Name}</b>, using <b><a href=\"https://github.com/DoogeJ/OpenAITelegramBot/\">OpenAITelegramBot</a></b> and the <code>{settings.Connections.OpenAIAPI.Model}</code>-model.{Environment.NewLine}{Environment.NewLine}" +
                                   $"⚙️ I am configured to keep <b>~{settings.Connections.OpenAIAPI.TokensToKeep} tokens</b> or <b>~{settings.Connections.OpenAIAPI.MinutesToKeep} minutes</b> of conversation history.{Environment.NewLine}{Environment.NewLine}" +
                                   $"{(prompts.Count == 1 ? "💭 I don't currently have any messages in memory." : $"💭 I'm currently keeping <b>~{tokensInMemory} tokens</b> and <b>{messagesInMemory} messages</b> in memory.{Environment.NewLine}⏳ My oldest message is from <b>{oldestMessageTime.Minutes}</b> minutes ago.")}{Environment.NewLine}{Environment.NewLine}" +
                                   $"🔆 I was last restarted <b>{bootTime.ToString("%d")} days, {bootTime.ToString("%h")} hours and {bootTime.ToString("%m")} minutes</b> ago.";

            await bot.SendTextMessageAsync(chatId: chatId, text: statusMessage, replyToMessageId: update.Message.MessageId, cancellationToken: ct, disableWebPagePreview: true, parseMode: ParseMode.Html);
            return;
        }

        if (update.Message.Type == MessageType.Photo)
        {
            // get average resolution photo
            PhotoSize photo = update.Message.Photo![(int)settings.Connections.TelegramAPI.PhotoQuality];
            Console.WriteLine($"< {update.Message.From?.FirstName} submitted an image of size: {photo.Height}x{photo.Width}. (Selected quality is {settings.Connections.TelegramAPI.PhotoQuality})");

            // create MemoryStream to receive a photo
            await using var memoryStream = new MemoryStream();

            // download photo
            await botClient.GetInfoAndDownloadFileAsync(photo.FileId, memoryStream, ct);

            // reset MemoryStream index
            memoryStream.Seek(0, SeekOrigin.Begin);

            // convert received image to Base64
            byte[] image = memoryStream.ToArray();
            string base64ImageRepresentation = Convert.ToBase64String(image);
            memoryStream.Dispose();

            // send image prompt to OpenAI
            chatRequest = new ChatRequest(new List<OpenAI.Chat.Message> {new OpenAI.Chat.Message(Role.User, new List<Content>
            {
                prompt,
                new ImageUrl($"data:image/jpeg;base64,{base64ImageRepresentation}", settings.Connections.TelegramAPI.PhotoQuality == PhotoQuality.Low ? ImageDetail.Low : ImageDetail.High)
            })}, model, user: update.Message.From?.FirstName, maxTokens: settings.Connections.OpenAIAPI.TokensToKeep);

            result = await api.ChatEndpoint.GetCompletionAsync(chatRequest);

            prompts.Add((Role.User, new List<Content>
            {
                prompt,
                new ImageUrl($"data:image/jpeg;base64,{base64ImageRepresentation}", ImageDetail.Auto)
            }, DateTime.UtcNow, (int)result.Usage.PromptTokens!));
        }
        else
        {
            // send text prompt to OpenAI
            chatRequest = new ChatRequest(new List<OpenAI.Chat.Message> { new OpenAI.Chat.Message(Role.User, prompt) }, model, user: update.Message.From?.FirstName, maxTokens: settings.Connections.OpenAIAPI.TokensToKeep);
            result = await api.ChatEndpoint.GetCompletionAsync(chatRequest);

            prompts.Add((Role.User, $"{update.Message.From!.FirstName} says: {prompt}", DateTime.UtcNow, (int)result.Usage.PromptTokens!));
        }

        if (result.FirstChoice.Message == null || result.FirstChoice.Message == "")
        {
            prompts.Add((Role.Assistant, "Sorry, I do not know an answer to this. Perhaps try asking differently.", DateTime.UtcNow, 16));
            Telegram.Bot.Types.Message sentMessage = await botClient.SendTextMessageAsync(
                               chatId: chatId,
                               text: "Sorry, I do not know an answer to this. Perhaps try asking differently.",
                               cancellationToken: cts.Token,
                               replyToMessageId: update.Message.MessageId);

            Console.WriteLine($"> {settings.Personality.Name}: Sorry, I do not know an answer to this. Perhaps try asking differently.");
            return;
        }

        string answer = result.FirstChoice.Message!.ToString().Trim();
        answer = answer.Replace($"{settings.Personality.Name} says: ", "");
        prompts.Add((Role.Assistant, answer, DateTime.UtcNow, (int)result.Usage.CompletionTokens!));
        Console.WriteLine($"> {settings.Personality.Name}: {answer}");

        await bot.SendTextMessageAsync(chatId, answer, replyToMessageId: update.Message.MessageId, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"While handling {update.Message?.Text}: {ex}");
    }
}

public enum PhotoQuality
{
    Low,
    Medium,
    High
}

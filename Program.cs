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
var prompts = new List<(OpenAI.Role role, string content, DateTime stamp)>
{
    (Role.System, settings.Personality.Prompt, DateTime.UtcNow)
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
        if (update.Message?.Type != MessageType.Text) return;

        // check if allowed chat
        if (settings.Connections.TelegramAPI.AllowedChats != null && settings.Connections.TelegramAPI.AllowedChats.Count() > 0 && !settings.Connections.TelegramAPI.AllowedChats.Contains(update.Message.Chat.Id)) return;
        if (update.Message.Chat.Type == ChatType.Private && !settings.Connections.TelegramAPI.AllowPrivateMessages) return;
        long chatId = update.Message.Chat.Id;
        string prompt = update.Message.Text!;

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
        else if (!settings.Personality.RespondToName || !prompt.Contains(settings.Personality.Name, StringComparison.OrdinalIgnoreCase))
        {
            //ignore this message
            return;
        }

        if (update.Message.Text!.Length > settings.Connections.TelegramAPI.MessageLengthLimit)
        {
            Telegram.Bot.Types.Message sentMessage = await botClient.SendTextMessageAsync(
                               chatId: chatId,
                               text: "Sorry but this message is too long for me to parse.",
                               cancellationToken: cts.Token,
                               replyToMessageId: update.Message.MessageId);

            Console.WriteLine("A: Sorry but this message is too long for me to parse.");
            return;
        }

        Console.WriteLine($"< {update.Message.From?.FirstName}: {update.Message.Text}");

        //we're going to answer! Send a typing indicator...
        await bot.SendChatActionAsync(update.Message.Chat.Id, ChatAction.Typing, null, ct);

        while (prompts.Count > 1 && prompts[1].stamp < (DateTime.UtcNow).AddMinutes(settings.Connections.OpenAIAPI.MinutesToKeep * -1)) prompts.RemoveAt(1);
        while (prompts.Sum(p => p.content.Length) / 3 > settings.Connections.OpenAIAPI.TokensToKeep) prompts.RemoveAt(1);

        if (prompt.StartsWith("/status"))
        {
            TimeSpan bootTime = DateTime.UtcNow.Subtract(prompts[0].stamp);
            var tokensInMemory = prompts.Count > 1 ? (prompts.Sum(p => p.content.Length) - prompts[0].content.Length) / 3 : 0;
            var messagesInMemory = prompts.Count - 1;
            TimeSpan oldestMessageTime = prompts.Count > 1 ? DateTime.UtcNow.Subtract(prompts[1].stamp) : TimeSpan.Zero;

            string statusMessage = $"ℹ️ This is <b>{settings.Personality.Name}</b>, using <b><a href=\"https://github.com/DoogeJ/OpenAITelegramBot/\">OpenAITelegramBot</a></b> and the <code>{settings.Connections.OpenAIAPI.Model}</code>-model.{Environment.NewLine}{Environment.NewLine}" +
                                   $"⚙️ I am configured to keep <b>~{settings.Connections.OpenAIAPI.TokensToKeep} tokens</b> or <b>~{settings.Connections.OpenAIAPI.MinutesToKeep} minutes</b> of conversation history.{Environment.NewLine}{Environment.NewLine}" +
                                   $"{(prompts.Count == 1 ? "💭 I don't currently have any messages in memory." : $"💭 I'm currently keeping <b>~{tokensInMemory} tokens</b> and <b>{messagesInMemory} messages</b> in memory.{Environment.NewLine}⏳ My oldest message is from <b>{oldestMessageTime.Minutes}</b> minutes ago.")}{Environment.NewLine}{Environment.NewLine}" +
                                   $"🔆 I was last restarted <b>{bootTime.ToString("%d")} days, {bootTime.ToString("%h")} hours and {bootTime.ToString("%m")} minutes</b> ago.";

            await bot.SendTextMessageAsync(chatId: chatId, text: statusMessage, replyToMessageId: update.Message.MessageId, cancellationToken: ct, disableWebPagePreview: true, parseMode: ParseMode.Html);
            return;
        }

        prompts.Add((Role.User, $"{update.Message.From!.FirstName} says: {prompt}", DateTime.UtcNow));

        ChatRequest chatRequest = new ChatRequest(prompts.Select(p => new OpenAI.Chat.Message(p.role, p.content)), model, user: update.Message.From?.FirstName);

        ChatResponse result = await api.ChatEndpoint.GetCompletionAsync(chatRequest);

        if (result.FirstChoice.Message == null || result.FirstChoice.Message == "")
        {
            prompts.Add((Role.Assistant, "Sorry, I do not know an answer to this. Perhaps try asking differently.", DateTime.UtcNow));
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
        prompts.Add((Role.Assistant, answer, DateTime.UtcNow));
        Console.WriteLine($"> {settings.Personality.Name}: {answer}");

        await bot.SendTextMessageAsync(chatId, answer, replyToMessageId: update.Message.MessageId, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"While handling {update.Message?.Text}: {ex}");
    }
}

using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Requests;

namespace OpenAITelegramBot
{
    public class Prompt(OpenAI.Role role, dynamic content, DateTime stamp, int tokens = 0)
    {
        public OpenAI.Role Role { get; set; } = role;
        public dynamic Content { get; set; } = content;
        public DateTime Stamp { get; set; } = stamp;
        public int Tokens { get; set; } = tokens;
    }

    internal class Program
    {
        static IConfiguration? config;
        static Settings? settings;
        static TelegramBotClient? botClient;
        static User? botUser;
        static CancellationTokenSource? botCts;
        static Model? openAIModel;
        static OpenAIClient? openAIClient;
        static readonly List<Prompt> openAIMessages = [];

        static void InitializeSettings()
        {
            config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            settings = config.GetRequiredSection("Settings").Get<Settings>()!;

            Console.WriteLine($"Starting {settings.Personality.Name}...");
            Console.Title = settings.Personality.Name;
        }

        static async Task<bool> InitializeTelegramAsync()
        {
            botClient = new TelegramBotClient(settings!.Connections.TelegramAPI.Token);
            botUser = await botClient.GetMeAsync();

            if (settings.Connections.TelegramAPI.Username.Equals($"@{botUser.Username}", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Connected to Telegram as {settings.Connections.TelegramAPI.Username} with ID {botUser.Id}.");
            }
            else
            {
                Console.WriteLine($"Error: Connected to Telegram as @{botUser.Username} with ID {botUser.Id}, but configured name is {settings.Connections.TelegramAPI.Username}!");
                return false;
            }

            botCts = new CancellationTokenSource();
            Telegram.Bot.Polling.ReceiverOptions receiverOptions = new() { AllowedUpdates = [UpdateType.Message, UpdateType.ChatMember] };

            botClient.StartReceiving(HandleTelegramMessage, HandlePollingErrorAsync, receiverOptions, botCts.Token);
            return true;
        }

        static async Task<bool> InitializeOpenAI()
        {
            openAIModel = new(settings!.Connections.OpenAIAPI.Model, "openai");
            openAIClient = new OpenAIClient(settings.Connections.OpenAIAPI.Token);

            //call with just the system prompt to determine prompt token cost
            ChatRequest chatRequest = new ChatRequest(new List<OpenAI.Chat.Message> { new OpenAI.Chat.Message(Role.System, settings.Personality.Prompt) }, model: openAIModel, maxTokens: settings.Connections.OpenAIAPI.TokensToKeep);
            ChatResponse result = await openAIClient.ChatEndpoint.GetCompletionAsync(chatRequest);

            openAIMessages.Add(new Prompt(Role.System, settings.Personality.Prompt, DateTime.UtcNow, (int)result.Usage.PromptTokens!));
            return true;
        }

        static async Task Main(string[] args)
        {
            InitializeSettings();
            await InitializeOpenAI();

            if (!await InitializeTelegramAsync())
                return;

            while (Console.ReadKey().Key != ConsoleKey.Escape) { }

            botCts!.Cancel();
        }

        static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"[Error {apiRequestException.ErrorCode}] {apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(ErrorMessage);
            return Task.CompletedTask;
        }

        static async Task<string> SendChatRequest(string user, dynamic message, Role role = Role.User)
        {
            string answer = "Sorry, I do not know an answer to this. Perhaps try asking differently.";

            //create a temporary buffer since we don't know token count yet
            message = $"{user} says: {message}";
            List<OpenAI.Chat.Message> conversation = [.. openAIMessages.Select(p => new OpenAI.Chat.Message(p.Role, p.Content))];
            conversation.Add(new OpenAI.Chat.Message(role, message));

            //request completion
            ChatRequest chatRequest = new(conversation, openAIModel, user: user, maxTokens: settings!.Connections.OpenAIAPI.TokensToKeep);
            ChatResponse result = await openAIClient!.ChatEndpoint.GetCompletionAsync(chatRequest);

            if (!(result.FirstChoice.Message == null || result.FirstChoice.Message == ""))
                answer = result.FirstChoice.Message;

            //commit to conversation history
            openAIMessages.Add(new Prompt(role, message, DateTime.UtcNow, (int)result.Usage.PromptTokens!));
            openAIMessages.Add(new Prompt(Role.Assistant, answer, DateTime.UtcNow, (int)result.Usage.CompletionTokens!));

            return answer;
        }

        static async Task<string> SendPhotoRequest(string user, dynamic message, Role role = Role.User)
        {
            string answer = "Sorry, I do not know an answer to this. Perhaps try asking differently.";

            //create a temporary buffer since we don't know token count yet
            List<OpenAI.Chat.Message> conversation = [.. openAIMessages.Select(p => new OpenAI.Chat.Message(p.Role, p.Content))];
            conversation.Add(new OpenAI.Chat.Message(role, message));

            //request completion
            ChatRequest chatRequest = new(conversation, openAIModel, user: user, maxTokens: settings!.Connections.OpenAIAPI.TokensToKeep);
            ChatResponse result = await openAIClient!.ChatEndpoint.GetCompletionAsync(chatRequest);

            if (!(result.FirstChoice.Message == null || result.FirstChoice.Message == ""))
                answer = result.FirstChoice.Message;

            //commit to conversation history
            openAIMessages.Add(new Prompt(role, message, DateTime.UtcNow, (int)result.Usage.PromptTokens!));
            openAIMessages.Add(new Prompt(Role.Assistant, answer, DateTime.UtcNow, (int)result.Usage.CompletionTokens!));

            return answer;
        }

        static async Task HandleTelegramMessage(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            // clean up to old messages
            while (openAIMessages.Count > 1 && openAIMessages[1].Stamp < DateTime.UtcNow.AddMinutes(settings!.Connections.OpenAIAPI.MinutesToKeep * -1)) openAIMessages.RemoveAt(1);

            // clean up messages when over token limit
            while (openAIMessages.Sum(p => p.Tokens) > settings!.Connections.OpenAIAPI.TokensToKeep) openAIMessages.RemoveAt(1);

            long chatId = update.Message!.Chat.Id;

            //only allow supported message types
            if (!(update.Message?.Type == MessageType.Text || (settings!.Connections.OpenAIAPI.VisionSupport && update.Message?.Type == MessageType.Photo))) return;

            //check for allowed chats
            if (settings!.Connections.TelegramAPI.AllowedChats != null && settings.Connections.TelegramAPI.AllowedChats.Length > 0 && !settings.Connections.TelegramAPI.AllowedChats.Contains(chatId)) return;
            if (update.Message!.Chat.Type == ChatType.Private && !settings.Connections.TelegramAPI.AllowPrivateMessages) return;

            //get message prompt
            string prompt = "";
            if (update.Message!.Type == MessageType.Text)
            {
                prompt = update.Message!.Text!;
            }
            else
            {
                prompt = update.Message!.Caption!;
            }

            prompt ??= "";

            //check if message should be responded to
            if (prompt.StartsWith(settings.Connections.TelegramAPI.Username, StringComparison.OrdinalIgnoreCase))
            {
                //call with @, strip username
                prompt = prompt.Replace(settings.Connections.TelegramAPI.Username, "", StringComparison.OrdinalIgnoreCase).Trim();
            }
            else if (update.Message.ReplyToMessage?.From?.Id == botUser!.Id)
            {
                //reply
            }
            else if (update.Message.Chat.Type == ChatType.Private)
            {
                //private message
            }
            else if (!settings.Personality.RespondToName
             && update.Message.ReplyToMessage?.From?.Id != botUser!.Id
              && !prompt.StartsWith(settings.Connections.TelegramAPI.Username, StringComparison.OrdinalIgnoreCase)
               || !prompt.Contains(settings.Personality.Name, StringComparison.OrdinalIgnoreCase)
                && update.Message.ReplyToMessage?.From?.Id != botUser!.Id)
            {
                //ignore in all other cases
                return;
            }

            //check for message length limit
            if (prompt.Length > settings.Connections.TelegramAPI.MessageLengthLimit)
            {
                Telegram.Bot.Types.Message sentMessage = await botClient!.SendTextMessageAsync(
                                   chatId: chatId,
                                   text: "Sorry but this message is too long for me to parse.",
                                   cancellationToken: botCts!.Token,
                                   replyToMessageId: update.Message.MessageId);

                Console.WriteLine("A: Sorry but this message is too long for me to parse.");
                return;
            }

            //we're going to answer! Send a typing indicator...
            await bot.SendChatActionAsync(update.Message.Chat.Id, ChatAction.Typing, null, ct);

            //@TODO: clean up old messages (datetime, tokens)

            //status message requested, disregard all else
            if (prompt.StartsWith("/status"))
            {
                TimeSpan bootTime = DateTime.UtcNow.Subtract(openAIMessages[0].Stamp);
                var tokensInMemory = openAIMessages.Count > 1 ? (openAIMessages.Sum(p => p.Tokens) - openAIMessages[0].Tokens) : 0;
                var messagesInMemory = openAIMessages.Count - 1;
                TimeSpan oldestMessageTime = openAIMessages.Count > 1 ? DateTime.UtcNow.Subtract(openAIMessages[1].Stamp) : TimeSpan.Zero;

                string statusMessage = $"ℹ️ This is <b>{settings.Personality.Name}</b>, using <b><a href=\"https://github.com/DoogeJ/OpenAITelegramBot/\">OpenAITelegramBot</a></b> and the <code>{settings.Connections.OpenAIAPI.Model}</code>-model.{Environment.NewLine}{Environment.NewLine}" +
                                       $"⚙️ I am configured to keep <b>~{settings.Connections.OpenAIAPI.TokensToKeep} tokens</b> or <b>~{settings.Connections.OpenAIAPI.MinutesToKeep} minutes</b> of conversation history.{Environment.NewLine}{Environment.NewLine}" +
                                       $"{(openAIMessages.Count == 1 ? "💭 I don't currently have any messages in memory." : $"💭 I'm currently keeping <b>~{tokensInMemory} tokens</b> and <b>{messagesInMemory} messages</b> in memory.{Environment.NewLine}⏳ My oldest message is from <b>{oldestMessageTime.Minutes}</b> minutes ago.")}{Environment.NewLine}{Environment.NewLine}" +
                                       $"🔆 I was last restarted <b>{bootTime.ToString("%d")} days, {bootTime.ToString("%h")} hours and {bootTime.ToString("%m")} minutes</b> ago.";

                await bot.SendTextMessageAsync(chatId: chatId, text: statusMessage, replyToMessageId: update.Message.MessageId, cancellationToken: ct, disableWebPagePreview: true, parseMode: ParseMode.Html);
                return;
            }

            //handle text message
            if (update.Message.Type == MessageType.Text)
            {
                Console.WriteLine($"< {update.Message.From?.FirstName!}: {update.Message.Text!}");
                string answer = await SendChatRequest(update.Message.From?.FirstName!, update.Message.Text!);
                await bot.SendTextMessageAsync(chatId, answer, replyToMessageId: update.Message.MessageId, cancellationToken: ct);
                Console.WriteLine($"> {settings!.Personality.Name}: {answer}");
            }

            //handle photo message
            if (update.Message.Type == MessageType.Photo)
            {
                // get photo
                PhotoSize photo = update.Message.Photo![(int)settings.Connections.TelegramAPI.PhotoQuality];
                Console.WriteLine($"< {update.Message.From?.FirstName} submitted an image of size: {photo.Height}x{photo.Width}. (Selected quality is {settings.Connections.TelegramAPI.PhotoQuality})");
                Console.WriteLine($"< {update.Message.From?.FirstName}: {update.Message.Caption}");
                // create MemoryStream to receive a photo
                await using var memoryStream = new MemoryStream();

                // download photo
                await botClient!.GetInfoAndDownloadFileAsync(photo.FileId, memoryStream, ct);

                // reset MemoryStream index
                memoryStream.Seek(0, SeekOrigin.Begin);

                // convert received image to Base64
                byte[] image = memoryStream.ToArray();
                string base64ImageRepresentation = Convert.ToBase64String(image);
                memoryStream.Dispose();

                // send image prompt to OpenAI
                var msg = new List<Content>
                    {
                        update.Message.Caption,
                        new ImageUrl($"data:image/jpeg;base64,{base64ImageRepresentation}", settings.Connections.TelegramAPI.PhotoQuality == PhotoQuality.Low ? ImageDetail.Low : ImageDetail.High)
                    };

                string answer = await SendPhotoRequest(update.Message.From?.FirstName!, msg);
                await bot.SendTextMessageAsync(chatId, answer, replyToMessageId: update.Message.MessageId, cancellationToken: ct);
                Console.WriteLine($"> {settings!.Personality.Name}: {answer}");
            }
        }
    }
}
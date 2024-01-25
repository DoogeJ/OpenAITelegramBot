namespace OpenAITelegramBot
{
    public enum PhotoQuality
    {
        Low,
        Medium,
        High
    }

    public sealed class Settings
    {
        public required Personality Personality { get; set; } = null!;
        public required Connections Connections { get; set; } = null!;
    }

    public sealed class Personality
    {
        public required string Name { get; set; }
        public required string Prompt { get; set; }
        public required bool RespondToName { get; set; }
    }

    public sealed class Connections
    {
        public required OpenAIAPI OpenAIAPI { get; set; } = null!;
        public required TelegramAPI TelegramAPI { get; set; } = null!;
    }

    public sealed class OpenAIAPI
    {
        public required string Token { get; set; }
        public required int MinutesToKeep { get; set; }
        public required int TokensToKeep { get; set; }
        public required string Model { get; set; }
        public required bool VisionSupport { get; set; }
    }

    public sealed class TelegramAPI
    {
        public required string Token { get; set; }
        public required string Username { get; set; }
        public required long[] AllowedChats { get; set; }
        public required int MessageLengthLimit { get; set; }
        public required bool AllowPrivateMessages { get; set; }
        public required PhotoQuality PhotoQuality { get; set; }
    }
}
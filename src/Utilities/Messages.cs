namespace IPK25_CHAT;

public class Messages
{
	private readonly ILogger<Messages> _logger;

	public Messages(ILogger<Messages> logger)
	{
		_logger = logger;
	}

	// Combined Command Parsing and Type Enum
	public enum CommandParseResultType
	{
		Unknown,
		Auth,
		Join,
		Rename,
		Help,
		ChatMessage
	}

	// Class to hold the result of parsing user input
	public class ParsedUserInput
	{
		public CommandParseResultType Type { get; set; }

		public string OriginalInput { get; set; }
		public string Username { get; set; }
		public string DisplayName { get; set; }
		public string Secret { get; set; }
		public string ChannelId { get; set; }
	}

	// The method HandleUserInputAsync expects
	public ParsedUserInput ParseUserInput(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
		}

		input = input.Trim();

		// Message
		if (!input.StartsWith("/"))
		{
			if (!Utils.IsValidContent(input))
			{
				_logger.LogWarning("Invalid characters or format for chat message: {Input}", input);
				Console.WriteLine("ERROR: Chat message contains invalid characters or is too long.");
				// Return unknown 
				return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
			}

			// ChatMessage
			return new ParsedUserInput { Type = CommandParseResultType.ChatMessage, OriginalInput = input };
		}

		// command
		string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0) // Should not happen if input wasn't whitespace and starts with /
		{
			return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
		}

		string command = parts[0].ToLowerInvariant();

		switch (command)
		{
			case "/auth":
				if (parts.Length != 4)
				{
					_logger.LogWarning("Invalid /auth command structure. Expected 3 parameters. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid /auth command. Use: /auth <Username> <Secret> <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				// Use Utils for validation before creating the result
				if (!Utils.IsValidId(parts[1]) || !Utils.IsValidSecret(parts[2]) || !Utils.IsValidDisplayName(parts[3]))
				{
					_logger.LogWarning("Invalid parameter format/characters in /auth command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid parameter format/characters in /auth command. Use: /auth <Username> <Secret> <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				return new ParsedUserInput
				{
					Type = CommandParseResultType.Auth,
					Username = parts[1],
					Secret = parts[2],
					DisplayName = parts[3],
					OriginalInput = input
				};


			case "/join":
				if (parts.Length != 2)
				{
					_logger.LogWarning("Invalid /join command structure. Expected 1 parameter. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid /join command. Use: /join <ChannelID>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				if (!Utils.IsValidId(parts[1]))
				{
					_logger.LogWarning("Invalid ChannelID format/characters in /join command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid ChannelID format/characters in /join command. Use: /join <ChannelID>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				return new ParsedUserInput
				{
					Type = CommandParseResultType.Join,
					ChannelId = parts[1],
					OriginalInput = input
				};


			case "/rename":
				if (parts.Length != 2)
				{
					_logger.LogWarning("Invalid /rename command structure. Expected 1 parameter. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid /rename command. Use: /rename <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				if (!Utils.IsValidDisplayName(parts[1]))
				{
					_logger.LogWarning("Invalid DisplayName format/characters in /rename command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid DisplayName format/characters in /rename command. Use: /rename <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				return new ParsedUserInput
				{
					Type = CommandParseResultType.Rename,
					DisplayName = parts[1],
					OriginalInput = input
				};


			case "/help":
				if (parts.Length != 1)
				{
					_logger.LogWarning("Invalid /help command structure. Expected no parameters. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid /help command. Use: /help");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				return new ParsedUserInput { Type = CommandParseResultType.Help, OriginalInput = input };

			default:
				_logger.LogWarning("Unknown command: {Command}. Input: {Input}", command, input);
				Console.WriteLine($"ERROR: Unknown command '{command}'. Use /help for available commands.");
				return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
		}
	}
}
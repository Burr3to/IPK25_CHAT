namespace IPK25_CHAT.Utilities;

// This class is responsible for parsing raw user input strings into structured commands or messages.
public class UserInputParser // Renamed from Messages
{
	// Logger instance for logging warnings and errors during parsing.
	private readonly ILogger<UserInputParser> _logger; // Logger type updated

	// Enum defining the different types of commands or messages that can result from parsing user input.
	public enum CommandParseResultType
	{
		Unknown,     // Input could not be parsed or was invalid
		Auth,        // /auth command
		Join,        // /join command
		Rename,      // /rename command (local command example)
		Help,        // /help command (local command example)
		ChatMessage  // Any input not starting with /
	}

	// Class to hold the structured result after parsing user input.
	public class ParsedUserInput
	{
		public CommandParseResultType Type { get; set; } // Uses the nested enum

		public string OriginalInput { get; set; }

		// Parameters for specific commands
		public string Username { get; set; }
		public string DisplayName { get; set; }
		public string Secret { get; set; }
		public string ChannelId { get; set; }
	}

	// Constructor that injects the logger.
	public UserInputParser(ILogger<UserInputParser> logger) // Logger type updated
	{
		_logger = logger;
	}

	// Parses a raw string of user input into a ParsedUserInput object.
	// Determines if the input is a known command or a chat message,
	// extracts parameters, and performs basic validation using ProtocolValidation methods.
	public ParsedUserInput ParseUserInput(string input)
	{
		// Handle null, empty, or whitespace input immediately.
		if (string.IsNullOrWhiteSpace(input))
		{
			// Referencing the nested enum
			return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
		}

		// Trim leading/trailing whitespace.
		input = input.Trim();

		// Check if the input is a chat message (doesn't start with '/').
		if (!input.StartsWith("/"))
		{
			// Validate chat message content using ProtocolValidation.
			if (!ProtocolValidation.IsValidContent(input)) // Call static method
			{
				_logger.LogWarning("Invalid characters or format for chat message: {Input}", input);
				Console.WriteLine("ERROR: Chat message contains invalid characters or is too long.");
				// Return Unknown type for invalid chat message content.
				return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
			}

			// Input is a valid chat message.
			return new ParsedUserInput { Type = CommandParseResultType.ChatMessage, OriginalInput = input };
		}

		// Input starts with '/', so it's a command.
		string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		if (parts.Length == 0)
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

				// Validate parameters using ProtocolValidation.
				if (!ProtocolValidation.IsValidId(parts[1]) || !ProtocolValidation.IsValidSecret(parts[2]) || !ProtocolValidation.IsValidDisplayName(parts[3])) // Call static methods
				{
					_logger.LogWarning("Invalid parameter format/characters in /auth command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid parameter format/characters in /auth command. Use: /auth <Username> <Secret> <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				// Parsing successful for /auth.
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

				// Validate ChannelID using ProtocolValidation.
				if (!ProtocolValidation.IsValidId(parts[1])) // Call static method
				{
					_logger.LogWarning("Invalid ChannelID format/characters in /join command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid ChannelID format/characters in /join command. Use: /join <ChannelID>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				// Parsing successful for /join.
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

				// Validate DisplayName using ProtocolValidation.
				if (!ProtocolValidation.IsValidDisplayName(parts[1])) // Call static method
				{
					_logger.LogWarning("Invalid DisplayName format/characters in /rename command. Input: {Input}", input);
					Console.WriteLine("ERROR: Invalid DisplayName format/characters in /rename command. Use: /rename <DisplayName>");
					return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
				}

				// Parsing successful for /rename.
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

				// Parsing successful for /help.
				return new ParsedUserInput { Type = CommandParseResultType.Help, OriginalInput = input };

			// Handle unknown commands.
			default:
				_logger.LogWarning("Unknown command: {Command}. Input: {Input}", command, input);
				Console.WriteLine($"ERROR: Unknown command '{command}'. Use /help for available commands.");
				return new ParsedUserInput { Type = CommandParseResultType.Unknown, OriginalInput = input };
		}
	}
}
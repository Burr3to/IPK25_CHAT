using Microsoft.Extensions.Logging; // Make sure you have this using statement
using System.Text.RegularExpressions;
using System; // For StringSplitOptions if needed later

namespace IPK25_CHAT;

// Client States remain the same
public enum ClientState
{
	Start,
	Connecting,
	Connected,
	Authenticating,
	Joined,
	Joining,
	End
}

public static class Utils
{
	// Constants for Message Structure
	public const string CRLF = "\r\n";
	public const int MaxIdLength = 20; // toto ma byt?
	public const int MaxSecretLength = 128;
	public const int MaxContentLength = 60000;
	public const int MaxDisplayNameLength = 20;

	// Regular Expressions for Validation (using spec constraints)
	public static readonly string IdRegexPattern = $"^[a-zA-Z0-9_-]{{1,{MaxIdLength}}}$";
	public static readonly string SecretRegexPattern = $"^[a-zA-Z0-9_-]{{1,{MaxSecretLength}}}$";
	public static readonly string DisplayNameRegexPattern = $"^[\\x21-\\x7E]{{1,{MaxDisplayNameLength}}}$"; // Printable ASCII 33-126

	// Basic check for allowed characters in content
	public static readonly string BasicContentCharRegexPattern = "^[\\x20-\\x7E\\n]+$"; // Printable ASCII, Space, Line Feed (\n)

	// Message Templates (Ensuring they end with CRLF)
	public static readonly string ErrMessageTemplate = "ERR FROM {0} IS {1}" + CRLF;
	public static readonly string AuthMessageTemplate = "AUTH {0} AS {1} USING {2}" + CRLF;
	public static readonly string JoinMessageTemplate = "JOIN {0} AS {1}" + CRLF;
	public static readonly string MsgMessageTemplate = "MSG FROM {0} IS {1}" + CRLF;
	public static readonly string ByeMessageTemplate = "BYE FROM {0}" + CRLF;

	// Regex objects for performance
	private static readonly Regex IdRegex = new(IdRegexPattern);
	private static readonly Regex SecretRegex = new(SecretRegexPattern);
	private static readonly Regex DisplayNameRegex = new(DisplayNameRegexPattern);
	private static readonly Regex BasicContentCharRegex = new(BasicContentCharRegexPattern);


	// Validation Methods
	public static bool IsValidId(string id) => id != null && IdRegex.IsMatch(id);
	public static bool IsValidSecret(string secret) => secret != null && SecretRegex.IsMatch(secret);
	public static bool IsValidDisplayName(string displayName) => displayName != null && DisplayNameRegex.IsMatch(displayName);

	// Simplified content check: Validates characters and checks length separately
	public static bool IsValidContent(string content)
	{
		if (string.IsNullOrEmpty(content) || content.Length > MaxContentLength)
			return false;
		foreach (char c in content)
		{
			// Allow 0x20-0x7E (Printable ASCII + Space) and 0x0A (LF)
			if (!((c >= '\x20' && c <= '\x7E') || c == '\n'))
			{
				return false;
			}
		}

		return true;
	}

	// --- Formatting Methods with Truncation ---

	// Helper to truncate and warn (visible within the assembly)
	internal static string Truncate(string value, int maxLength, string fieldName, ILogger logger)
	{
		if (value != null && value.Length > maxLength)
		{
			string truncatedValue = value.Substring(0, maxLength);
			logger.LogWarning("Local: {FieldName} truncated from {OriginalLength} to {MaxLength} characters.", fieldName, value.Length, maxLength);
			Console.WriteLine($"Warning: {fieldName} was too long and has been truncated to {maxLength} characters.");
			return truncatedValue;
		}

		return value;
	}

	// Note: Formatting methods now accept ILogger for truncation warnings

	public static string FormatErrorMessage(string displayName, string messageContent, ILogger logger)
	{
		var truncatedDisplayName = Truncate(displayName, MaxDisplayNameLength, "ERR DisplayName", logger);
		var truncatedContent = Truncate(messageContent, MaxContentLength, "ERR MessageContent", logger);
		// Basic validation after potential truncation
		if (!IsValidDisplayName(truncatedDisplayName) || string.IsNullOrEmpty(truncatedContent)) return null;
		return string.Format(ErrMessageTemplate, truncatedDisplayName, truncatedContent);
	}

	public static string FormatAuthMessage(string username, string displayName, string secret, ILogger logger)
	{
		var tUsername = Truncate(username, MaxIdLength, "AUTH Username", logger);
		var tDisplayName = Truncate(displayName, MaxDisplayNameLength, "AUTH DisplayName", logger);
		var tSecret = Truncate(secret, MaxSecretLength, "AUTH Secret", logger);

		if (!IsValidId(tUsername) || !IsValidDisplayName(tDisplayName) || !IsValidSecret(tSecret))
		{
			logger.LogError("Local: Invalid parameters for AUTH message after potential truncation.");
			return null;
		}

		return string.Format(AuthMessageTemplate, tUsername, tDisplayName, tSecret);
	}

	public static string FormatJoinMessage(string channelId, string displayName, ILogger logger)
	{
		var tChannelId = Truncate(channelId, MaxIdLength, "JOIN ChannelID", logger);
		var tDisplayName = Truncate(displayName, MaxDisplayNameLength, "JOIN DisplayName", logger);

		if (!IsValidId(tChannelId) || !IsValidDisplayName(tDisplayName))
		{
			logger.LogError("Local: Invalid parameters for JOIN message after potential truncation.");
			return null;
		}

		return string.Format(JoinMessageTemplate, tChannelId, tDisplayName);
	}

	public static string FormatMsgMessage(string displayName, string messageContent, ILogger logger)
	{
		var tDisplayName = Truncate(displayName, MaxDisplayNameLength, "MSG DisplayName", logger);
		var tMessageContent = Truncate(messageContent, MaxContentLength, "Message Content", logger);

		if (!IsValidDisplayName(tDisplayName) || !IsValidContent(tMessageContent)) // Use IsValidContent here
		{
			logger.LogError("Local: Invalid parameters for MSG message after potential truncation.");
			return null;
		}

		return string.Format(MsgMessageTemplate, tDisplayName, tMessageContent);
	}

	public static string FormatByeMessage(string displayName, ILogger logger)
	{
		var tDisplayName = Truncate(displayName, MaxDisplayNameLength, "BYE DisplayName", logger);
		if (!IsValidDisplayName(tDisplayName))
		{
			logger.LogError("Local: Invalid DisplayName for BYE message after potential truncation.");
			return null;
		}

		return string.Format(ByeMessageTemplate, tDisplayName);
	}

	// Class to hold parsed server message info
	public class ParsedServerMessage
	{
		//tcp
		public ServerMessageType Type { get; set; } = ServerMessageType.Unknown; // Default
		public bool IsOkReply { get; set; } // Only for REPLY
		public string DisplayName { get; set; } // For MSG, ERR, BYE
		public string Content { get; set; } // For REPLY, MSG, ERR
		public string OriginalMessage { get; set; } // For logging/debugging

		//+ udp things
		public byte UdpMessageType { get; init; } // The raw message type code (e.g., 0x01 for REPLY)
		public ushort MessageId { get; init; } // The MessageID from the received datagram header
		public IPEndPoint Sender { get; init; } // Who sent this message (IP Address and Port)
		public ushort? RefMessageId { get; set; } // For CONFIRM, REPLY: ID of the message being referenced
		public bool? ReplyResult { get; set; } // For REPLY: True if OK, False if NOK
		public string MessageContent { get; set; } // For REPLY, MSG, ERR - SET is correct. Use this primarily for UDP content.
	}

	public enum MessagesSizeBytes
	{
		// Included space for "0"
		// Later add size for contents
		Confirm = 3,
		Reply = 7,
		Auth = 4,
		Join = 4,
		Msg = 4,
		Err = 4,
		Bye = 4,
		Ping = 3
	}

	public enum MessageType : byte
	{
		ConfirmType = 0x00,
		ReplyType = 0x01,
		AuthType = 0x02,
		JoinType = 0x03,
		MsgType = 0x04,
		PingType = 0xFD,
		ErrType = 0xFE,
		ByeType = 0xFF
	}

	// Enum for parsed server message types
	public enum ServerMessageType
	{
		Unknown,
		Reply,
		Msg,
		Err,
		Bye
	}

	// Regex for parsing incoming messages based on ABNF structure
	private static readonly Regex
		ReplyRegex = new(@"^REPLY (?<Status>OK|NOK) IS(?<Content>.*)$", RegexOptions.Singleline); // Relaxed space after IS for robustness? No, spec says SP. Let's keep it strict.

	// private static readonly Regex ReplyRegex = new(@"^REPLY (?<Status>OK|NOK) IS (?<Content>.*)$", RegexOptions.Singleline); // Strict space
	private static readonly Regex MsgRegex = new(@"^MSG FROM (?<DName>[\x21-\x7E]{1,20}) IS (?<Content>.*)$", RegexOptions.Singleline);
	private static readonly Regex ErrRegex = new(@"^ERR FROM (?<DName>[\x21-\x7E]{1,20}) IS (?<Content>.*)$", RegexOptions.Singleline);
	private static readonly Regex ByeRegex = new(@"^BYE FROM (?<DName>[\x21-\x7E]{1,20})$", RegexOptions.Singleline);


	// The missing parsing method
	public static ParsedServerMessage ParseServerMessage(string message, ILogger logger)
	{
		var result = new ParsedServerMessage { OriginalMessage = message }; // Default type is Unknown

		if (string.IsNullOrWhiteSpace(message))
		{
			logger.LogWarning("Received empty or whitespace message line.");
			return result; // Return Unknown type
		}

		// Trim potentially remaining whitespace just in case, although ReadLineAsync should handle CRLF.
		message = message.Trim();

		Match match;

		// IMPORTANT: Check REPLY first as its content can contain keywords like "MSG FROM"
		match = ReplyRegex.Match(message);
		if (match.Success)
		{
			result.Type = ServerMessageType.Reply;
			result.IsOkReply = match.Groups["Status"].Value == "OK";
			result.Content = match.Groups["Content"].Value.TrimStart();
			return result;
		}

		match = MsgRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			// Re-validate display name from message itself
			if (!IsValidDisplayName(dName))
			{
				logger.LogWarning("Received MSG with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed)
			}

			result.Type = ServerMessageType.Msg;
			result.DisplayName = dName;
			result.Content = match.Groups["Content"].Value.TrimStart();
			return result;
		}

		match = ErrRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			if (!IsValidDisplayName(dName))
			{
				logger.LogWarning("Received ERR with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed)
			}

			result.Type = ServerMessageType.Err;
			result.DisplayName = dName;
			result.Content = match.Groups["Content"].Value.TrimStart();
			return result;
		}

		match = ByeRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			if (!IsValidDisplayName(dName))
			{
				logger.LogWarning("Received BYE with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed)
			}

			result.Type = ServerMessageType.Bye;
			result.DisplayName = dName;
			// No Content for BYE
			return result;
		}

		// If none matched
		logger.LogWarning("Received unparseable/malformed message line: {Message}", message);
		return result; // Return Unknown type
	}


	public static IPAddress GetFirstIPv4Address(string _serverHost)
	{
		// 1. Try parsing as a direct IP address (both IPv4 and IPv6)
		if (IPAddress.TryParse(_serverHost, out IPAddress directIpAddress))
		{
			if (directIpAddress.AddressFamily == AddressFamily.InterNetwork)
			{
				return directIpAddress; // It's a valid IPv4 address
			}
			else
			{
				return null;
			}
		}

		// 2. If not a direct IP address, treat it as a hostname and resolve DNS
		IPAddress[] addresses = Dns.GetHostAddresses(_serverHost);

		if (addresses != null && addresses.Length > 0)
		{
			IPAddress ipv4Address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

			if (ipv4Address != null)
			{
				return ipv4Address;
			}
			else
			{
				// No IPv4 address found for the hostname
				return null; // Or throw an exception
			}
		}
		else
		{
			// No IP addresses found for the hostname
			return null; // Or throw an exception
		}
	}


	public static void SetState(ref ClientState currentState, ClientState newState, ILogger logger)
	{
		if (currentState != newState)
		{
			logger.LogDebug("State transition: {OldState} -> {NewState}", currentState, newState);
			currentState = newState;
		}
	}

	public static void PrintHelp()
	{
		Console.WriteLine("--- Client Commands ---");
		Console.WriteLine("/auth <Username> <Secret> <DisplayName> - Authenticate with the server.");
		Console.WriteLine("    <Username>, <Secret>: [a-zA-Z0-9_-], max 20/128 chars.");
		Console.WriteLine("    <DisplayName>: Printable ASCII (no spaces), max 20 chars.");
		Console.WriteLine("/join <ChannelID> - Join a specific channel.");
		Console.WriteLine("    <ChannelID>: [a-zA-Z0-9_-], max 20 chars.");
		Console.WriteLine("/rename <DisplayName> - Change your display name locally.");
		Console.WriteLine("    <DisplayName>: Printable ASCII (no spaces), max 20 chars.");
		Console.WriteLine("/help - Display this help message.");
		Console.WriteLine("Any other input is sent as a chat message to the current channel (after successful /auth).");
		Console.WriteLine("Use Ctrl+C or Ctrl+D to exit.");
		Console.WriteLine("-----------------------");
	}
}
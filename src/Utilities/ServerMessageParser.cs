namespace IPK25_CHAT.Utilities;

// Class to hold parsed server message info (both TCP and UDP fields)
public class ParsedServerMessage
{
	// --- TCP Fields ---
	// The parsed type of the TCP message (e.g., Reply, Msg, Err, Bye).
	public ServerMessageType Type { get; set; } = ServerMessageType.Unknown;

	// Indicates the status of a TCP REPLY message (True for OK, False for NOK).
	public bool IsOkReply { get; set; }

	public string DisplayName { get; set; }
	public string Content { get; set; }

	// The raw incoming message string
	public string OriginalMessage { get; set; }

	// --- UDP Fields ---
	// The message type byte code (e.g., MessageType.ReplyType) from a UDP datagram header.
	public byte UdpMessageType { get; init; }

	public ushort MessageId { get; init; }
	public IPEndPoint Sender { get; init; }
	public ushort? RefMessageId { get; set; }
	public bool? ReplyResult { get; set; }
	public string MessageContent { get; set; } // Original name maintained
}

// Made by Gemini as stated in documentation
// Provides static methods for parsing incoming TCP messages from the server.
public static class ServerMessageParser // Made static
{
	// Regex for parsing incoming REPLY messages (case-insensitive).
	// Captures Status (OK/NOK) and Content. Uses Singleline and IgnoreCase options.
	private static readonly Regex ReplyRegex = new(@"^REPLY (?<Status>OK|NOK) IS(?<Content>.*)$", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

	// Regex for parsing incoming MSG messages (case-insensitive).
	// Captures Display Name and Content. Uses Singleline and IgnoreCase options.
	private static readonly Regex MsgRegex = new(@"^MSG FROM (?<DName>[\x21-\x7E]{1,20}) IS (?<Content>.*)$",
		RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

	// Regex for parsing incoming ERR messages (case-insensitive).
	// Captures Display Name and Content. Uses Singleline and IgnoreCase options.
	private static readonly Regex ErrRegex = new(@"^ERR FROM (?<DName>[\x21-\x7E]{1,20}) IS (?<Content>.*)$",
		RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

	// Regex for parsing incoming BYE messages (case-insensitive).
	// Captures Display Name. Uses IgnoreCase option.
	private static readonly Regex ByeRegex = new(@"^BYE FROM (?<DName>[\x21-\x7E]{1,20})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);


	// Parses an incoming TCP message string received from the server.
	// Identifies the message type and extracts relevant fields (Status, DisplayName, Content).
	// Logs warnings for empty, whitespace, or unparseable messages.
	// Returns a ParsedServerMessage object. Type will be Unknown if parsing fails or the message is malformed.
	// Requires an ILogger instance passed in, as static classes cannot have instance loggers.
	public static ParsedServerMessage ParseServerMessage(string message, ILogger logger) // Added logger parameter
	{
		var result = new ParsedServerMessage { OriginalMessage = message };

		// Handle empty or whitespace messages early
		if (string.IsNullOrWhiteSpace(message))
		{
			logger.LogWarning("Received empty or whitespace message line.");
			return result; // Returns Unknown type
		}

		// Trim whitespace from start/end for reliable matching
		message = message.Trim();

		Match match;

		// Attempt to match message types in a logical order.
		// REPLY must be checked first.

		match = ReplyRegex.Match(message);
		if (match.Success)
		{
			result.Type = ServerMessageType.Reply;
			result.IsOkReply = match.Groups["Status"].Value.Equals("OK", StringComparison.OrdinalIgnoreCase);
			result.Content = match.Groups["Content"].Value.TrimStart();
			return result;
		}

		match = MsgRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			// Re-validate display name from message itself using ProtocolValidation
			if (!ProtocolValidation.IsValidDisplayName(dName))
			{
				logger.LogWarning("Received MSG with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed according to spec)
			}

			result.Type = ServerMessageType.Msg;
			result.DisplayName = dName;
			result.Content = match.Groups["Content"].Value.TrimStart(); // Trim leading space from content
			return result;
		}

		match = ErrRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			if (!ProtocolValidation.IsValidDisplayName(dName))
			{
				logger.LogWarning("Received ERR with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed)
			}

			result.Type = ServerMessageType.Err;
			result.DisplayName = dName;
			result.Content = match.Groups["Content"].Value.TrimStart(); // Trim leading space from content
			return result;
		}

		match = ByeRegex.Match(message);
		if (match.Success)
		{
			string dName = match.Groups["DName"].Value;
			if (!ProtocolValidation.IsValidDisplayName(dName))
			{
				logger.LogWarning("Received BYE with invalid DisplayName format: {DisplayName}", dName);
				return result; // Return Unknown type (malformed)
			}

			result.Type = ServerMessageType.Bye;
			result.DisplayName = dName;
			// BYE has no content field
			return result;
		}

		// If none of the known message formats match
		logger.LogWarning("Received unparseable/malformed message line: {Message}", message);
		return result; // Returns Unknown type
	}
}
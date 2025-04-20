namespace IPK25_CHAT.Utilities;

// Provides static methods and constants for validating chat protocol fields.
//  Regex functionality made by Gemini as stated in documentation

public static class ProtocolValidation
{
	// --- Protocol Constants ---
	// Standard Carriage Return and Line Feed sequence used for message termination in TCP.
	public const string CRLF = "\r\n";

	// Maximum length for Username, Channel ID, and similar ID fields.
	public const int MaxIdLength = 20;

	// Maximum length for the user's secret/password.
	public const int MaxSecretLength = 128;

	// Maximum length for chat message content and error message content.
	public const int MaxContentLength = 60000;

	// Maximum length for a user's display name.
	public const int MaxDisplayNameLength = 20;

	// --- Regular Expressions for Validation ---
	// Regex pattern for validating Username and Channel ID fields ([a-zA-Z0-9_-], length 1-MaxIdLength).
	public static readonly string IdRegexPattern = $"^[a-zA-Z0-9_-]{{1,{MaxIdLength}}}$";

	// Regex pattern for validating Secret fields ([a-zA-Z0-9_-], length 1-MaxSecretLength).
	public static readonly string SecretRegexPattern = $"^[a-zA-Z0-9_-]{{1,{MaxSecretLength}}}$";

	// Regex pattern for validating Display Name fields (Printable ASCII 0x21-0x7E, length 1-MaxDisplayNameLength).
	public static readonly string DisplayNameRegexPattern = $"^[\\x21-\\x7E]{{1,{MaxDisplayNameLength}}}$"; // Printable ASCII 33-126

	// Note: IsValidContent method handles the actual content character validation.

	// --- Compiled Regex Objects for Performance ---
	private static readonly Regex IdRegex = new(IdRegexPattern, RegexOptions.Compiled);
	private static readonly Regex SecretRegex = new(SecretRegexPattern, RegexOptions.Compiled);
	private static readonly Regex DisplayNameRegex = new(DisplayNameRegexPattern, RegexOptions.Compiled);


	// --- Validation Methods ---
	// Checks if a string is a valid Username or Channel ID.
	public static bool IsValidId(string id) => id != null && IdRegex.IsMatch(id);

	// Checks if a string is a valid Secret.
	public static bool IsValidSecret(string secret) => secret != null && SecretRegex.IsMatch(secret);

	// Checks if a string is a valid Display Name.
	public static bool IsValidDisplayName(string displayName) => displayName != null && DisplayNameRegex.IsMatch(displayName);

	// Validates if a string is valid chat message content.
	// Checks for allowed characters (Printable ASCII + Space + LF) and maximum length.
	public static bool IsValidContent(string content)
	{
		if (string.IsNullOrEmpty(content) || content.Length > MaxContentLength)
			return false;

		// Character-by-character check for allowed characters
		foreach (char c in content)
		{
			// Allowed characters are 0x20-0x7E (Printable ASCII + Space) and 0x0A (LF)
			if (!((c >= '\x20' && c <= '\x7E') || c == '\n'))
			{
				return false;
			}
		}

		return true;
	}
}
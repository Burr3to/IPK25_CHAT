using System;
using Microsoft.Extensions.Logging;
// Add necessary using directives for ProtocolValidation

namespace IPK25_CHAT;

// Provides static methods for formatting data into protocol-compliant TCP message strings.
public static class ClientMessageFormatter // Made static
{
    // --- Message Template Strings (Outgoing) ---
    // Using string.Format templates ending with CRLF (using CRLF from ProtocolValidation)

    // Format template for outgoing ERR messages.
    public static readonly string ErrMessageTemplate = "ERR FROM {0} IS {1}" + ProtocolValidation.CRLF;

    // Format template for outgoing AUTH messages.
    public static readonly string AuthMessageTemplate = "AUTH {0} AS {1} USING {2}" + ProtocolValidation.CRLF;

    // Format template for outgoing JOIN messages.
    public static readonly string JoinMessageTemplate = "JOIN {0} AS {1}" + ProtocolValidation.CRLF;

    // Format template for outgoing MSG messages.
    public static readonly string MsgMessageTemplate = "MSG FROM {0} IS {1}" + ProtocolValidation.CRLF;

    // Format template for outgoing BYE messages.
    public static readonly string ByeMessageTemplate = "BYE FROM {0}" + ProtocolValidation.CRLF;


    // --- Formatting Methods (Outgoing) ---

    // Helper method to truncate a string to a maximum length and log a warning if truncation occurs.
    // Requires an ILogger instance passed in.
    public static string Truncate(string value, int maxLength, string fieldName, ILogger logger) // Added logger parameter
    {
        if (value != null && value.Length > maxLength)
        {
            string truncatedValue = value.Substring(0, maxLength);
            logger.LogWarning("Local: {FieldName} truncated from {OriginalLength} to {MaxLength} characters.", fieldName, value.Length, maxLength);
            Console.WriteLine($"ERROR: {fieldName} was too long and has been truncated to {maxLength} characters."); // Keep original Console.WriteLine
            return truncatedValue;
        }

        return value;
    }

    // Formats an ERR message string according to the protocol specification.
    // Truncates parameters if necessary and validates the result using ProtocolValidation.
    // Returns null if parameters are invalid even after truncation.
    // Requires an ILogger instance passed in.
    public static string FormatErrorMessage(string displayName, string messageContent, ILogger logger) // Added logger parameter
    {
        var truncatedDisplayName = Truncate(displayName, ProtocolValidation.MaxDisplayNameLength, "ERR DisplayName", logger);
        var truncatedContent = Truncate(messageContent, ProtocolValidation.MaxContentLength, "ERR MessageContent", logger);

        // Basic validation after potential truncation.
        if (!ProtocolValidation.IsValidDisplayName(truncatedDisplayName) || string.IsNullOrEmpty(truncatedContent))
        {
            logger.LogError("Local: Invalid parameters for ERR message after potential truncation.");
            return null;
        }

        return string.Format(ErrMessageTemplate, truncatedDisplayName, truncatedContent);
    }

    // Formats an AUTH message string according to the protocol specification.
    // Truncates parameters if necessary and validates the result using ProtocolValidation.
    // Returns null if parameters are invalid even after truncation.
    // Requires an ILogger instance passed in.
    public static string FormatAuthMessage(string username, string displayName, string secret, ILogger logger) // Added logger parameter
    {
        var tUsername = Truncate(username, ProtocolValidation.MaxIdLength, "AUTH Username", logger);
        var tDisplayName = Truncate(displayName, ProtocolValidation.MaxDisplayNameLength, "AUTH DisplayName", logger);
        var tSecret = Truncate(secret, ProtocolValidation.MaxSecretLength, "AUTH Secret", logger);

        if (!ProtocolValidation.IsValidId(tUsername) || !ProtocolValidation.IsValidDisplayName(tDisplayName) || !ProtocolValidation.IsValidSecret(tSecret))
        {
            logger.LogError("Local: Invalid parameters for AUTH message after potential truncation.");
            return null;
        }

        return string.Format(AuthMessageTemplate, tUsername, tDisplayName, tSecret);
    }

    // Formats a JOIN message string according to the protocol specification.
    // Truncates parameters if necessary and validates the result using ProtocolValidation.
    // Returns null if parameters are invalid even after truncation.
    // Requires an ILogger instance passed in.
    public static string FormatJoinMessage(string channelId, string displayName, ILogger logger) // Added logger parameter
    {
        var tChannelId = Truncate(channelId, ProtocolValidation.MaxIdLength, "JOIN ChannelID", logger);
        var tDisplayName = Truncate(displayName, ProtocolValidation.MaxDisplayNameLength, "JOIN DisplayName", logger);

        if (!ProtocolValidation.IsValidId(tChannelId) || !ProtocolValidation.IsValidDisplayName(tDisplayName))
        {
            logger.LogError("Local: Invalid parameters for JOIN message after potential truncation.");
            return null;
        }

        return string.Format(JoinMessageTemplate, tChannelId, tDisplayName);
    }

    // Formats a MSG message string according to the protocol specification.
    // Truncates parameters if necessary and validates the result using ProtocolValidation.
    // Returns null if parameters are invalid even after truncation.
    // Requires an ILogger instance passed in.
    public static string FormatMsgMessage(string displayName, string messageContent, ILogger logger) // Added logger parameter
    {
        var tDisplayName = Truncate(displayName, ProtocolValidation.MaxDisplayNameLength, "MSG DisplayName", logger);
        var tMessageContent = Truncate(messageContent, ProtocolValidation.MaxContentLength, "Message Content", logger);

        if (!ProtocolValidation.IsValidDisplayName(tDisplayName) || !ProtocolValidation.IsValidContent(tMessageContent))
        {
            logger.LogError("Local: Invalid parameters for MSG message after potential truncation.");
            return null;
        }

        return string.Format(MsgMessageTemplate, tDisplayName, tMessageContent);
    }

    // Formats a BYE message string according to the protocol specification.
    // Truncates parameters if necessary and validates the result using ProtocolValidation.
    // Returns null if parameters are invalid even after truncation.
    // Requires an ILogger instance passed in.
    public static string FormatByeMessage(string displayName, ILogger logger) // Added logger parameter
    {
        var tDisplayName = Truncate(displayName, ProtocolValidation.MaxDisplayNameLength, "BYE DisplayName", logger);
        if (!ProtocolValidation.IsValidDisplayName(tDisplayName))
        {
            logger.LogError("Local: Invalid DisplayName for BYE message after potential truncation.");
            return null;
        }

        return string.Format(ByeMessageTemplate, tDisplayName);
    }
}
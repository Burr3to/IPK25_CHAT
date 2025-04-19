using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
// Add necessary using directives for the enums if they were moved to separate files

namespace IPK25_CHAT;

// Provides general client utility methods (networking, state management, UI help)
// and core protocol-level enums/constants not specific to validation, parsing, or formatting.

// Defines the possible states of the chat client connection/authentication process.
public enum ClientState
{
    Start,
    Connected, // Connection established, ready to authenticate
    Authenticating, // AUTH message sent, waiting for REPLY
    Joining, // JOIN message sent, waiting for REPLY
    Joined, // Successfully joined a channel, ready to chat
    End // Client shutting down or disconnected
}

// Defines the byte codes for UDP message types based on the protocol specification.
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

// Defines the fixed sizes of UDP message headers in bytes.
// These values include the MessageType byte and MessageID ushort where applicable.
public enum MessagesSizeBytes
{
    // Sizes might need adjustment based on exact protocol spec
    Confirm = 3, // MessageType (1) + MessageID (2)
    Reply = 7,   // MessageType (1) + MessageID (2) + RefMessageID (2) + ReplyResult (1) -- check spec!
    Auth = 6,    // MessageType (1) + MessageID (2) + VarDataLen (2) -- check spec!
    Join = 5,    // MessageType (1) + MessageID (2) + VarDataLen (1) -- check spec!
    Msg = 5,     // MessageType (1) + MessageID (2) + VarDataLen (1) -- check spec!
    Err = 5,     // MessageType (1) + MessageID (2) + VarDataLen (1) -- check spec!
    Bye = 4,     // MessageType (1) + MessageID (2) + VarDataLen (1) -- check spec!
    Ping = 3     // MessageType (1) + MessageID (2)
}


// Defines the parsed types for incoming TCP server messages.
// Used by the ServerMessageParser.
public enum ServerMessageType
{
    Unknown, // Message could not be parsed or was malformed
    Reply,
    Msg,
    Err,
    Bye
}


// Provides general client utility methods that don't belong in more specific classes.
public static class Utils // Remains static
{
    // --- Network Helpers ---
    // Resolves a hostname or IP address string to the first available IPv4 address.
    // Handles direct IP parsing first, then DNS lookup.
    // Returns the first found IPv4 address, or null if none found or host is invalid.
    public static IPAddress GetFirstIPv4Address(string _serverHost)
    {
        // 1. Try parsing as a direct IP address
        if (IPAddress.TryParse(_serverHost, out IPAddress directIpAddress))
        {
            // Check if it's IPv4
            if (directIpAddress.AddressFamily == AddressFamily.InterNetwork)
                return directIpAddress;

            // It was an IP but not IPv4
            return null;
        }

        // 2. Treat as hostname and resolve DNS
        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(_serverHost);

            // Find the first IPv4 address
            IPAddress ipv4Address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            return ipv4Address; // Returns null if no IPv4 found or addresses array is empty/null
        }
        catch (SocketException)
        {
            // DNS resolution failed
            return null;
        }
        catch (ArgumentException)
        {
            // Hostname format is invalid
            return null;
        }
    }

    // --- State Management Helpers ---
    // Updates the client's state and logs the transition.
    // Requires an ILogger instance passed in.
    public static void SetState(ref ClientState currentState, ClientState newState, ILogger logger) // Added logger parameter
    {
        if (currentState != newState)
        {
            logger.LogDebug("State transition: {OldState} -> {NewState}", currentState, newState);
            currentState = newState;
        }
    }

    // --- UI and Help Methods ---
    // Prints the client command usage help message to the console.
    public static void PrintHelp()
    {
        Console.WriteLine("--- Client Commands ---");
        // Reference constants from ProtocolValidation
        Console.WriteLine($"/auth <Username> <Secret> <DisplayName> - Authenticate with the server.");
        Console.WriteLine($"    <Username>, <Secret>: [a-zA-Z0-9_-], max {ProtocolValidation.MaxIdLength}/{ProtocolValidation.MaxSecretLength} chars.");
        Console.WriteLine($"    <DisplayName>: Printable ASCII (0x21-0x7E), max {ProtocolValidation.MaxDisplayNameLength} chars.");
        Console.WriteLine($"/join <ChannelID> - Join a specific channel.");
        Console.WriteLine($"    <ChannelID>: [a-zA-Z0-9_-], max {ProtocolValidation.MaxIdLength} chars.");
        Console.WriteLine($"/rename <DisplayName> - Change your display name locally.");
        Console.WriteLine($"    <DisplayName>: Printable ASCII (0x21-0x7E), max {ProtocolValidation.MaxDisplayNameLength} chars.");
        Console.WriteLine("/help - Display this help message.");
        Console.WriteLine("Any other input is sent as a chat message to the current channel (after successful /auth and /join).");
        Console.WriteLine("Use Ctrl+C or Ctrl+D to exit.");
        Console.WriteLine("-----------------------");
    }
}
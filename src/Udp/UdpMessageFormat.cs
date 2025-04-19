using System;
using System.Net; // For IPAddress.HostToNetworkOrder
using System.Text; // For Encoding.ASCII
// Add necessary using directives for MessageType and MessagesSizeBytes enums
using IPK25_CHAT; // Assuming MessageType and MessagesSizeBytes are in this namespace

namespace IPK25_CHAT.Udp;

// Provides static methods for manually formatting outgoing UDP messages into byte arrays,
// according to the defined chat protocol structure.
public static class UdpMessageFormat
{
	// Formats a CONFIRM message packet.
	public static byte[] FormatConfirmManually(ushort refMessageId)
	{
		/* UDP CONFIRM Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+
		|  0x00  |  Ref_MessageID  |
		+--------+--------+--------+
		*/

		// Calculate the total size of the packet based on the defined enum constant.
		int totalSize = (int)MessagesSizeBytes.Confirm;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.ConfirmType;

		// Write Reference MessageID (2 bytes), ensuring network byte order (big-endian).
		// BitConverter.GetBytes returns bytes in host byte order (little-endian on most PCs).
		// IPAddress.HostToNetworkOrder converts a short (Int16) to network byte order.
		byte[] refMessageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)refMessageId));
		Array.Copy(refMessageIdBytes, 0, messageData, currentIndex, refMessageIdBytes.Length);
		currentIndex += 2;

		// Basic verification: Check if the number of bytes written matches the calculated total size.
		if (currentIndex != totalSize)
		{
			// Log or output an error if there's a mismatch (indicates a bug in formatting logic).
			Console.WriteLine($"[ManualFormatConfirm] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Return null to indicate formatting failure.
		}

		return messageData;
	}

	// Formats an AUTH message packet.
	public static byte[] FormatAuthManually(ushort messageId, string username, string displayName, string secret)
	{
		/* UDP AUTH Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+-----~~-----+---+-------~~------+---+----~~----+---+
		|  0x02  |    MessageID    |  Username  | 0 |  DisplayName  | 0 |  Secret  | 0 |
		+--------+--------+--------+-----~~-----+---+-------~~------+---+----~~----+---+
		(Username, DisplayName, Secret are null-terminated ASCII strings)
		*/

		// Encode string contents to byte arrays using ASCII encoding.
		byte[] usernameBytes = Encoding.ASCII.GetBytes(username ?? string.Empty); // Handle null input defensively
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);
		byte[] secretBytes = Encoding.ASCII.GetBytes(secret ?? string.Empty);

		// Calculate the total size: Base header size + length of each string + 3 null terminators (1 byte each).
		// Note: MessagesSizeBytes.Auth likely only includes the fixed header (Type + ID).
		int totalSize = (int)MessagesSizeBytes.Auth + usernameBytes.Length + 1 + displayNameBytes.Length + 1 + secretBytes.Length + 1;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.AuthType;

		// Write MessageID (2 bytes) in network byte order.
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Write Username bytes followed by a null terminator (1 byte 0x00).
		Array.Copy(usernameBytes, 0, messageData, currentIndex, usernameBytes.Length);
		currentIndex += usernameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Write DisplayName bytes followed by a null terminator.
		Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
		currentIndex += displayNameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Write Secret bytes followed by a null terminator.
		Array.Copy(secretBytes, 0, messageData, currentIndex, secretBytes.Length);
		currentIndex += secretBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Basic verification: Check if the number of bytes written matches the calculated total size.
		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatAuth] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Indicate formatting failure.
		}

		return messageData;
	}

	// Formats a JOIN message packet.
	public static byte[] FormatJoinManually(ushort messageId, string channelId, string displayName)
	{
		/* UDP JOIN Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+-----~~-----+---+-------~~------+---+
		|  0x03  |    MessageID    |  ChannelID | 0 |  DisplayName  | 0 |
		+--------+--------+--------+-----~~-----+---+-------~~------+---+
		(ChannelID and DisplayName are null-terminated ASCII strings)
		*/

		// Encode string contents to byte arrays using ASCII encoding.
		byte[] channelIdBytes = Encoding.ASCII.GetBytes(channelId ?? string.Empty);
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);

		// Calculate total size: Base header size + length of strings + 2 null terminators.
		// Note: MessagesSizeBytes.Join likely only includes fixed header (Type + ID).
		int totalSize = (int)MessagesSizeBytes.Join + channelIdBytes.Length + 1 + displayNameBytes.Length + 1;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.JoinType;

		// Write MessageID (2 bytes) in network byte order.
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Write ChannelID bytes followed by a null terminator.
		Array.Copy(channelIdBytes, 0, messageData, currentIndex, channelIdBytes.Length);
		currentIndex += channelIdBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Write DisplayName bytes followed by a null terminator.
		Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
		currentIndex += displayNameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Basic verification.
		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatJoin] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Indicate formatting failure.
		}

		return messageData;
	}

	// Formats a MSG message packet.
	public static byte[] FormatMsgManually(ushort messageId, string displayName, string messageContents)
	{
		/* UDP MSG Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		|  0x04  |    MessageID    |  DisplayName  | 0 |  MessageContents  | 0 |
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		(DisplayName and MessageContents are null-terminated ASCII strings)
		*/

		// Encode string contents to byte arrays using ASCII encoding.
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);
		byte[] messageContentsBytes = Encoding.ASCII.GetBytes(messageContents ?? string.Empty);

		// Calculate total size: Base header size + length of strings + 2 null terminators.
		// Note: MessagesSizeBytes.Msg likely only includes fixed header (Type + ID).
		int totalSize = (int)MessagesSizeBytes.Msg + displayNameBytes.Length + 1 + messageContentsBytes.Length + 1;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.MsgType;

		// Write MessageID (2 bytes) in network byte order.
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Write DisplayName bytes followed by a null terminator.
		Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
		currentIndex += displayNameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Write MessageContents bytes followed by a null terminator.
		Array.Copy(messageContentsBytes, 0, messageData, currentIndex, messageContentsBytes.Length);
		currentIndex += messageContentsBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Basic verification.
		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatMsg] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Indicate formatting failure.
		}

		return messageData;
	}

	// Formats an ERR message packet.
	public static byte[] FormatErrManually(ushort messageId, string displayName, string messageContents)
	{
		/* UDP ERR Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		|  0xFE  |    MessageID    |  DisplayName  | 0 |  MessageContents  | 0 |
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		(DisplayName and MessageContents are null-terminated ASCII strings)
		*/

		// Encode string contents to byte arrays using ASCII encoding.
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);
		byte[] messageContentsBytes = Encoding.ASCII.GetBytes(messageContents ?? string.Empty);

		// Calculate total size: Base header size + length of strings + 2 null terminators.
		// Note: MessagesSizeBytes.Err likely only includes fixed header (Type + ID).
		int totalSize = (int)MessagesSizeBytes.Err + displayNameBytes.Length + 1 + messageContentsBytes.Length + 1;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.ErrType;

		// Write MessageID (2 bytes) in network byte order.
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Write DisplayName bytes followed by a null terminator.
		Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
		currentIndex += displayNameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Write MessageContents bytes followed by a null terminator.
		Array.Copy(messageContentsBytes, 0, messageData, currentIndex, messageContentsBytes.Length);
		currentIndex += messageContentsBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Basic verification.
		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatErr] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Indicate formatting failure.
		}

		return messageData;
	}

	// Formats a BYE message packet.
	public static byte[] FormatByeManually(ushort messageId, string displayName)
	{
		/* UDP BYE Message Format:
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+
		|  0xFF  |    MessageID    |  DisplayName  | 0 |
		+--------+--------+--------+-------~~------+---+
		(DisplayName is a null-terminated ASCII string)
		*/

		// Encode string contents to byte array using ASCII encoding.
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);

		// Calculate total size: Base header size + length of display name + 1 null terminator.
		// Note: MessagesSizeBytes.Bye likely only includes fixed header (Type + ID).
		int totalSize = (int)MessagesSizeBytes.Bye + displayNameBytes.Length + 1;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// Write Message Type (1 byte)
		messageData[currentIndex++] = (byte)MessageType.ByeType;

		// Write MessageID (2 bytes) in network byte order.
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Write DisplayName bytes followed by a null terminator.
		Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
		currentIndex += displayNameBytes.Length;
		messageData[currentIndex++] = 0x00; // Null terminator

		// Basic verification.
		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatBye] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Indicate formatting failure.
		}

		return messageData;
	}

}
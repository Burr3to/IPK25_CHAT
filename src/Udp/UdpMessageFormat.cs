using static IPK25_CHAT.Utils;

namespace IPK25_CHAT.Udp;

public class UdpMessageFormat
{
	

	public static byte[] FormatConfirmManually(ushort refMessageId)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+
		|  0x00  |  Ref_MessageID  |
		+--------+--------+--------+
		*/

		int totalSize = (int)MessagesSizeBytes.Confirm;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.ConfirmType;

		// Ref_MessageID 2 bytes with correct byte order
		byte[] refMessageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)refMessageId));
		Array.Copy(refMessageIdBytes, 0, messageData, currentIndex, refMessageIdBytes.Length);
		currentIndex += 2;

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatConfirm] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null;
		}

		return messageData;
	}

	public static byte[] FormatAuthManually(ushort messageId, string username, string displayName, string secret)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+-----~~-----+---+-------~~------+---+----~~----+---+
		|  0x02  |    MessageID    |  Username  | 0 |  DisplayName  | 0 |  Secret  | 0 |
		+--------+--------+--------+-----~~-----+---+-------~~------+---+----~~----+---+
		*/

		byte[] usernameBytes = Encoding.ASCII.GetBytes(username);
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName);
		byte[] secretBytes = Encoding.ASCII.GetBytes(secret);

		// Base Size + contents
		int totalSize = (int)MessagesSizeBytes.Auth + usernameBytes.Length + displayNameBytes.Length + secretBytes.Length;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.AuthType;

		// MessageID 2 bytes
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// Username 
		if (usernameBytes.Length > 0)
		{
			Array.Copy(usernameBytes, 0, messageData, currentIndex, usernameBytes.Length);
			currentIndex += usernameBytes.Length;
		}
		messageData[currentIndex++] = 0x00;

		// DisplayName
		if (displayNameBytes.Length > 0)
		{
			Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
			currentIndex += displayNameBytes.Length;
		}
		messageData[currentIndex++] = 0x00;

		// Secret
		if (secretBytes.Length > 0)
		{
			Array.Copy(secretBytes, 0, messageData, currentIndex, secretBytes.Length);
			currentIndex += secretBytes.Length;
		}
		messageData[currentIndex++] = 0x00;

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatAuth] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null;
		}

		return messageData;
	}

	public static byte[] FormatJoinManually(ushort messageId, string channelId, string displayName)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+-----~~-----+---+-------~~------+---+
		|  0x03  |    MessageID    |  ChannelID | 0 |  DisplayName  | 0 |
		+--------+--------+--------+-----~~-----+---+-------~~------+---+
		*/

		byte[] channelIdBytes = Encoding.ASCII.GetBytes(channelId ?? string.Empty);
		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);

		// Base Size + contents
		int totalSize = (int)MessagesSizeBytes.Join + channelIdBytes.Length + displayNameBytes.Length;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.JoinType; 

		// MessageID 2 bytes
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// ChannelID
		if (channelIdBytes.Length > 0)
		{
			Array.Copy(channelIdBytes, 0, messageData, currentIndex, channelIdBytes.Length);
			currentIndex += channelIdBytes.Length;
		}
		messageData[currentIndex++] = 0x00; // Null Terminator for ChannelID

		// DisplayName
		if (displayNameBytes.Length > 0)
		{
			Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
			currentIndex += displayNameBytes.Length;
		}
		messageData[currentIndex++] = 0x00; // Null Terminator for DisplayName

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatJoin] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Or throw an exception
		}

		return messageData;
	}

	public static byte[] FormatMsgManually(ushort messageId, string displayName, string messageContents)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		|  0x04  |    MessageID    |  DisplayName  | 0 |  MessageContents  | 0 |
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		*/

		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName);
		byte[] messageContentsBytes = Encoding.ASCII.GetBytes(messageContents);

		// Base Size + contents
		int totalSize = (int)MessagesSizeBytes.Msg + displayNameBytes.Length + messageContentsBytes.Length;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.MsgType;

		// MessageID 2 bytes
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// DisplayName
		if (displayNameBytes.Length > 0)
		{
			Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
			currentIndex += displayNameBytes.Length;
		}
		messageData[currentIndex++] = 0x00; 

		// MessageContents
		if (messageContentsBytes.Length > 0)
		{
			Array.Copy(messageContentsBytes, 0, messageData, currentIndex, messageContentsBytes.Length);
			currentIndex += messageContentsBytes.Length;
		}
		messageData[currentIndex++] = 0x00;

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatMsg] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null; // Or throw an exception
		}

		return messageData;
	}

	public static byte[] FormatErrManually(ushort messageId, string displayName, string messageContents)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		|  0xFE  |    MessageID    |  DisplayName  | 0 |  MessageContents  | 0 |
		+--------+--------+--------+-------~~------+---+--------~~---------+---+
		*/

		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName);
		byte[] messageContentsBytes = Encoding.ASCII.GetBytes(messageContents);

		// Base Size + contents
		int totalSize = (int)MessagesSizeBytes.Err + displayNameBytes.Length + messageContentsBytes.Length;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.ErrType;

		// MessageID 2 bytes
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// DisplayName
		if (displayNameBytes.Length > 0)
		{
			Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
			currentIndex += displayNameBytes.Length;
		}
		messageData[currentIndex++] = 0x00;

		// MessageContents
		if (messageContentsBytes.Length > 0)
		{
			Array.Copy(messageContentsBytes, 0, messageData, currentIndex, messageContentsBytes.Length);
			currentIndex += messageContentsBytes.Length;
		}
		messageData[currentIndex++] = 0x00; 

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatErr] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null;
		}

		return messageData;
	}

	public static byte[] FormatByeManually(ushort messageId, string displayName)
	{
		/*
		  1 byte       2 bytes
		+--------+--------+--------+-------~~------+---+
		|  0xFF  |    MessageID    |  DisplayName  | 0 |
		+--------+--------+--------+-------~~------+---+
		*/

		byte[] displayNameBytes = Encoding.ASCII.GetBytes(displayName ?? string.Empty);

		// Base Size + contents
		int totalSize = (int)MessagesSizeBytes.Bye + displayNameBytes.Length;
		byte[] messageData = new byte[totalSize];

		int currentIndex = 0;

		// 1 byte
		messageData[currentIndex++] = (byte)MessageType.ByeType;

		// MessageID 2 bytes
		byte[] messageIdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)messageId));
		Array.Copy(messageIdBytes, 0, messageData, currentIndex, messageIdBytes.Length);
		currentIndex += 2;

		// DisplayName
		if (displayNameBytes.Length > 0)
		{
			Array.Copy(displayNameBytes, 0, messageData, currentIndex, displayNameBytes.Length);
			currentIndex += displayNameBytes.Length;
		}

		messageData[currentIndex++] = 0x00;

		if (currentIndex != totalSize)
		{
			Console.WriteLine($"[ManualFormatBye] ERROR: Mismatch in calculated size ({totalSize}) and bytes written ({currentIndex}).");
			return null;
		}

		return messageData;
	}

	
}
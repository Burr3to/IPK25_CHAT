namespace IPK25_CHAT.Udp;

// Partial class definition for the UDP chat client, focusing on receiving and processing incoming messages.
public partial class UdpChatClient
{

	// --- Receiving Loop ---

	// Handles continuous receiving of UDP datagrams from any source.
	private async Task ReceiveMessagesUdpAsync(CancellationToken cancellationToken)
	{
		_logger.LogDebug("Starting UDP message receiver loop...");
		// Buffer large enough for maximum UDP packet size minus IP/UDP headers
		var buffer = new byte[65507];
		// EndPoint to capture the sender's address/port
		EndPoint senderEndPoint = new IPEndPoint(IPAddress.Any, 0);

		// Loop while cancellation is not requested, socket is valid, and client is not ending
		while (!cancellationToken.IsCancellationRequested && _socket != null && _currentState != ClientState.End)
		{
			try
			{
				// Receive data asynchronously, capturing the sender's endpoint
				var receiveResult = await _socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, senderEndPoint, cancellationToken);
				int bytesRead = receiveResult.ReceivedBytes;
				var currentSender = (IPEndPoint)receiveResult.RemoteEndPoint; // Get sender IP & Port

				// Minimum packet size is 3 bytes (Type + ID)
				if (bytesRead < 3)
				{
					_logger.LogWarning("Received runt datagram ({BytesRead} bytes) from {Sender}. Ignoring.", bytesRead, currentSender);
					continue; // Ignore malformed packet
				}

				// Extract header info: MessageType (1 byte), MessageID (2 bytes)
				byte messageType = buffer[0];
				ushort messageId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 1));

				_logger.LogTrace("Received {BytesRead} bytes from {Sender}. Type: 0x{Type:X2} ({MessageType}), ID: {MessageId}",
					bytesRead, currentSender, messageType, ((MessageType)messageType).ToString(), messageId); // Log MessageType enum name

				// Handle dynamic port update if needed (e.g., server sends back from a different port after AUTH)
				HandleDynamicPortUpdate(currentSender); // Defined below

				// --- UDP Reliability: Process Confirmation or Send Confirmation ---

				// If it's a CONFIRM message, handle it internally and don't send a CONFIRM back
				if (messageType == (byte)MessageType.ConfirmType)
				{
					HandleIncomingConfirm(messageId, bytesRead, currentSender); // MessageId of the CONFIRM is the RefID
					continue; // Skip sending a CONFIRM back for a CONFIRM packet
				}

				// For any other message type (AUTH, JOIN, MSG, REPLY, ERR, BYE, PING), send a CONFIRM back
				await SendConfirmationAsync(messageId, currentSender, CancellationToken.None); // Defined below

				// --- Process Specific Message Types ---

				// Use a switch statement to handle the different message types
				switch (messageType)
				{
					case (byte)MessageType.ReplyType:
						ParseAndHandleReply(buffer, bytesRead, messageId, currentSender); // Process REPLY
						break;
					case (byte)MessageType.MsgType:
						ParseAndHandleMsg(buffer, bytesRead, messageId, currentSender); // Process MSG
						break;
					case (byte)MessageType.PingType:
						_logger.LogDebug("Received PING (ID: {MessageId}) from {Sender}. Confirmation sent.", messageId, currentSender);
						// PING usually doesn't require further action besides confirmation
						break;
					case (byte)MessageType.ErrType:
						// ERR messages are critical and often lead to shutdown
						await ParseAndHandleErrAsync(buffer, bytesRead, messageId, currentSender); // Process ERR (Defined below)
						break;
					case (byte)MessageType.ByeType:
						// BYE messages indicate server shutdown, client should respond and shut down
						await ParseAndHandleByeAsync(buffer, bytesRead, messageId, currentSender); // Process BYE (Defined below)
						break;
					// AUTH and JOIN types would be received by a server, not a client in typical client-server interaction
					case (byte)MessageType.AuthType:
					case (byte)MessageType.JoinType:
						_logger.LogWarning("Received unexpected client message Type 0x{Type:X2} ({MessageType}) from {Sender}. Ignoring content.",
							messageType, ((MessageType)messageType).ToString(), currentSender);
						// Optionally send an ERR back for unexpected message types
						// await SendErrorAsync($"Received unexpected message type 0x{messageType:X2} ({((MessageType)messageType).ToString()}).", currentSender); // Defined in main partial
						break;
					default:
						// Handle unknown or unhandled message types
						_logger.LogWarning("Received unknown/unhandled message Type 0x{Type:X2} ({BytesRead} bytes) from {Sender}. Ignoring content.",
							messageType, bytesRead, currentSender);
						// Use specified internal error format
						Console.WriteLine("ERROR: Received unknown/unhandled server message.");
						// Send ERR back for unknown types
						await SendErrorAsync($"Protocol error: Received invalid message type 0x{messageType:X2}.", currentSender); // Defined in main partial
						// This might be a severe enough error to initiate client shutdown
						// await InitiateShutdownAsync($"Received unknown message type 0x{messageType:X2} from server.", false); // Defined in main partial
						break;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				_logger.LogInformation("UDP message receiver loop cancelled.");
				break; // Exit loop due to cancellation
			}
			catch (SocketException se)
			{
				// Handle socket errors during ReceiveFromAsync
				if (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
				{
					// This might indicate a persistent issue or server disconnect
					Console.WriteLine("ERROR: Connection to server appears to be lost.");
					_logger.LogError("Assuming server connection lost due to SocketException {ErrorCode} during receive. Initiating shutdown.", se.SocketErrorCode);
					await InitiateShutdownAsync("Server connection lost (SocketException during receive).", false); // Defined in main partial
					break; // Exit loop
				}
				else
				{
                     // Socket exception occurred because cancellation requested and socket is being closed/disposed
                     _logger.LogDebug("ReceiveFromAsync SocketException {ErrorCode} during shutdown/cancellation.", se.SocketErrorCode);
				}
			}
			catch (ObjectDisposedException)
			{
				// Expected if socket is disposed while ReceiveFromAsync is pending during shutdown
				_logger.LogWarning("Receive attempted on disposed socket. Exiting loop.");
				break; // Exit loop
			}
			catch (Exception ex) // Catch-all for unexpected errors in the receive loop
			{
				if (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
				{
					_logger.LogError(ex, "Unhandled exception in receiver loop.");
					Console.WriteLine($"ERROR: Critical error receiving data: {ex.Message}"); // Use specified format
					await InitiateShutdownAsync($"Critical error in receive loop: {ex.Message}", false); // Defined in main partial
				}
				else
				{
					// Log exception but note it happened during shutdown
					_logger.LogInformation("Exception in receiver loop, but cancellation/shutdown was in progress: {ExceptionMessage}", ex.Message);
				}

				break; // Exit loop
			}
		}

		_logger.LogDebug("Exiting UDP message receiver loop.");
	}

	// --- Incoming Message Handling & Parsing ---
	private void HandleDynamicPortUpdate(IPEndPoint currentSender)
	{
		if (_currentState == ClientState.Authenticating)
		{
			// Check if the sender's address OR port is different
			if (!_currentServerEndPoint.Equals(currentSender))
			{
				_logger.LogInformation(
					"Detected server endpoint switch from {OldEp} to {NewEp} while authenticating. Updating current target.",
					_currentServerEndPoint, currentSender);
				_currentServerEndPoint = currentSender;
			}
		}
	}

	// Handles an incoming CONFIRM packet. Signals the waiting TaskCompletionSource for the corresponding reliable message.
	private void HandleIncomingConfirm(ushort confirmRefId, int bytesRead, IPEndPoint sender)
	{
		// Validate the size of the CONFIRM packet
		if (bytesRead == (byte)MessagesSizeBytes.Confirm) // Correct size for CONFIRM is 3 bytes (Type + ID)
		{
			_logger.LogDebug("Received CONFIRM for MessageID: {RefMessageId} from {Sender}.", confirmRefId, sender);

			// Try to remove the corresponding pending confirm TCS
			if (_pendingConfirms.TryRemove(confirmRefId, out var confirmTcs)) // Remove *and* get the TCS
			{
				// Signal the waiting TaskCompletionSource that the confirmation was received
				confirmTcs.TrySetResult(true); // Signal success
				_logger.LogTrace("Signaled completion for pending confirm ID: {RefMessageId}", confirmRefId);
			}
			else
			{
				_logger.LogWarning("Received CONFIRM for unknown or already completed/removed MessageID: {RefMessageId}", confirmRefId);
			}
		}
		else
		{
			// Received a malformed CONFIRM packet
			_logger.LogWarning("Received malformed CONFIRM (size {BytesRead} != {ExpectedSize}) from {Sender}. Ignoring.",
				bytesRead, (byte)MessagesSizeBytes.Confirm, sender);
			Console.WriteLine("ERROR: Received malformed CONFIRM");
            SendErrorAsync($"Malformed CONFIRM packet size: {bytesRead}.", sender).GetAwaiter().GetResult(); // Sync call in async void context
		}
	}

	// Sends a CONFIRM packet back to the sender for a received message.
	// Called for most incoming message types except CONFIRM itself.
	private async Task SendConfirmationAsync(ushort receivedMessageId, IPEndPoint targetEndPoint, CancellationToken cancellationToken)
	{
		_logger.LogTrace("Preparing to send CONFIRM for received MessageID {ReceivedMessageId} to {TargetEndPoint}", receivedMessageId, targetEndPoint);

		// Format the CONFIRM packet using manual formatting
		byte[] confirmBytes = UdpMessageFormat.FormatConfirmManually(receivedMessageId); // Assumes UdpMessageFormat static class

		if (confirmBytes == null)
		{
			_logger.LogError("Failed to format CONFIRM message for received ID {ReceivedMessageId}. Message not sent.", receivedMessageId);
            Console.WriteLine($"ERROR: Internal error formatting CONFIRM message for received ID {receivedMessageId}."); // Use internal error format
			return; // Exit method
		}

		// Check socket and state before sending
		if (_socket == null || _currentState == ClientState.End)
		{
			_logger.LogWarning("Cannot send CONFIRM for received ID {ReceivedMessageId}, socket is null or client shutting down.", receivedMessageId);
			return; // Exit method
		}

		try
		{
			// Send the packet fire-and-forget (CONFIRMs are typically not reliable themselves)
			await _socket.SendToAsync(confirmBytes, SocketFlags.None, targetEndPoint, cancellationToken);
			_logger.LogDebug("Sent CONFIRM for received ID {ReceivedMessageId} to {TargetEndPoint}", receivedMessageId, targetEndPoint);
		}
		catch (OperationCanceledException)
		{
             _logger.LogDebug("Send CONFIRM operation cancelled.");
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Attempted to send CONFIRM for {ReceivedMessageId} on a disposed socket during shutdown.", receivedMessageId);
		}
		catch (SocketException se)
		{
			// Log network errors during send. Non-fatal for this fire-and-forget message.
			_logger.LogWarning(se, "SocketException (Code:{SocketErrorCode}) while sending CONFIRM for {ReceivedMessageId} to {TargetEndPoint}. Confirm likely not sent.",
				se.SocketErrorCode, receivedMessageId, targetEndPoint);
		}
		catch (Exception ex) // Catch-all for other unexpected errors
		{
			_logger.LogError(ex, "Unexpected exception while sending CONFIRM for {ReceivedMessageId} to {TargetEndPoint}.", receivedMessageId, targetEndPoint);
		}
	}


	// Parses and handles an incoming REPLY packet. Signals the waiting TaskCompletionSource for the corresponding request.
	private void ParseAndHandleReply(byte[] buffer, int bytesRead, ushort receivedMessageId, IPEndPoint sender)
	{
		_logger.LogDebug("Parsing received REPLY message (ID: {MessageId}, Size: {BytesRead}) from {Sender}", receivedMessageId, bytesRead, sender);

		// Validate minimum size based on MessageSizeBytes enum
		if (bytesRead < (int)MessagesSizeBytes.Reply)
		{
			_logger.LogWarning("Received malformed REPLY (ID: {MessageId}, too short: {Length} bytes) from {Sender}. Ignoring.", receivedMessageId, bytesRead, sender);
            // Optionally send ERR back for malformed packets
            // SendErrorAsync($"Malformed REPLY packet (too short: {bytesRead} bytes).", sender).GetAwaiter().GetResult(); // Sync call
			return; // Exit handling
		}

		try
		{
			// Extract fields from the byte buffer based on UDP protocol format
			byte resultByte = buffer[3];
			bool isOkReply = (resultByte == 1); // Assuming 1=OK, 0=NOK

			ushort refMessageId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 4)); // RefMessageID is 2 bytes after result byte

			int contentStartIndex = 6; // Start after Type(1) + ID(2) + Result(1) + RefID(2)
			int nullTerminatorIndex = -1;

			// Find the null terminator for the content string
			for (int i = contentStartIndex; i < bytesRead; i++)
			{
				if (buffer[i] == 0)
				{
					nullTerminatorIndex = i;
					break;
				}
			}

			// Content must be null-terminated and there must be data before the terminator
			if (nullTerminatorIndex == -1 || nullTerminatorIndex < contentStartIndex)
			{
				_logger.LogWarning("Received malformed REPLY (ID: {MessageId}, no or invalid null terminator for content) from {Sender}. Ignoring.", receivedMessageId, sender);
                 // Optionally send ERR back
                 // SendErrorAsync($"Malformed REPLY packet (missing content null terminator).", sender).GetAwaiter().GetResult(); // Sync call
				return; // Exit handling
			}

			// Extract the content string using ASCII encoding (or appropriate protocol encoding)
			int contentLength = nullTerminatorIndex - contentStartIndex;
			string messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, contentLength);

            // Optionally check for trailing null terminator after content + final packet size validation
            // if (nullTerminatorIndex + 1 != bytesRead) { ... malformed ... }

			// --- Create Parsed Message Object ---
			// Create a ParsedServerMessage object to hold the UDP-specific parsed data
			ParsedServerMessage parsedReply = new ParsedServerMessage
			{
				// UDP specific fields
				UdpMessageType = (byte)MessageType.ReplyType,
				MessageId = receivedMessageId,
				Sender = sender,
				ReplyResult = isOkReply,
				RefMessageId = refMessageId, // The ID of the original request this is replying to
				MessageContent = messageContent,

				// TCP-like fields (for consistency if needed elsewhere, but primary fields are UDP-specific)
				Type = ServerMessageType.Reply, // Map UDP REPLY type to TCP ServerMessageType.Reply
				IsOkReply = isOkReply,
				Content = messageContent // Map UDP content to TCP content field
			};

			_logger.LogInformation("Parsed REPLY ID:{MessageId} for ReqID:{RefMessageId} from {Sender}. Result:{Result}, Content:'{Content}'",
				parsedReply.MessageId, parsedReply.RefMessageId, parsedReply.Sender, parsedReply.ReplyResult.Value ? "OK" : "NOK", parsedReply.MessageContent);


			// --- Signal the Waiting Task ---
			// Check if we have a TaskCompletionSource waiting for a REPLY with this RefMessageID
			if (parsedReply.RefMessageId.HasValue && _pendingReplies.TryRemove(parsedReply.RefMessageId.Value, out var replyTcs)) // Remove *and* get the TCS
			{
				// Signal the waiting TaskCompletionSource with the parsed reply object
				bool signaled = replyTcs.TrySetResult(parsedReply);
				_logger.LogDebug("Signaled pending reply handler for original request ID {RefMessageId}. Success: {Signaled}", parsedReply.RefMessageId.Value, signaled);
			}
			else
			{
				// No corresponding TaskCompletionSource was found.
				// This happens if:
				// 1. The REPLY is a duplicate.
				// 2. The original request timed out while waiting for CONFIRM or REPLY.
				// 3. The original request was cancelled.
				// 4. The server sent a REPLY without a client request (protocol violation?).
				_logger.LogWarning("Received REPLY (ID:{MessageId}) for unknown, timed-out, or already completed/removed request ID: {RefMessageId}. Discarding.", receivedMessageId, parsedReply.RefMessageId);
                // No console output needed for duplicate/unexpected replies according to strict rules. Log only.
			}
		}
		catch (ArgumentOutOfRangeException aoorex) // LogError during BitConverter or GetString if indices are wrong
		{
			_logger.LogError(aoorex, "Parsing error (ArgumentOutOfRange) processing REPLY ID:{MessageId} from {Sender}. Check indices/lengths. BytesRead:{BytesRead}",
				receivedMessageId, sender, bytesRead);
             Console.WriteLine($"ERROR: Internal parsing error for incoming REPLY message from {sender}."); // Use internal error format
             // Optionally send ERR back
             // SendErrorAsync($"Internal error parsing REPLY packet.", sender).GetAwaiter().GetResult(); // Sync call
		}
		catch (Exception ex) // Catch-all for other potential parsing errors
		{
			_logger.LogError(ex, "Unexpected exception while parsing REPLY message ID:{MessageId} from {Sender}", receivedMessageId, sender);
             Console.WriteLine($"ERROR: Unexpected internal error parsing incoming REPLY message from {sender}."); // Use internal error format
             // Optionally send ERR back
             // SendErrorAsync($"Unexpected error parsing REPLY packet.", sender).GetAwaiter().GetResult(); // Sync call
		}
	}

	// Processes a functional REPLY (OK or NOK) after it has been received and parsed and matched to a pending request.
	// Updates client state based on the success/failure and the state when the original request was sent.
	private void ProcessFunctionalReply(ParsedServerMessage reply)
	{
		// This method is called *after* ParseAndHandleReply signals the TaskCompletionSource,
		_logger.LogDebug("Processing functional reply. Reply Success: {IsOkReply}, Content: '{Content}'. Current state: {CurrentState}",
			reply.ReplyResult ?? false, reply.MessageContent, _currentState);

		// Determine what operation this REPLY corresponds to based on the client's state

		bool wasAuthReply = (_currentState == ClientState.Authenticating); // Check state *before* processing reply content
		bool wasJoinReply = (_currentState == ClientState.Joining);

		// Store original channel ID before potential state change/clearing on JOIN failure
		string previousChannelIdOnJoin = _currentChannelId; // Capture before potential state update

		if (reply.ReplyResult == true) // Check the bool? field for OK status
		{
			// --- Handle Successful Replies (OK) ---
			Console.WriteLine($"Action Success: {reply.MessageContent}");

			if (wasAuthReply)
			{
				// AUTH Success
				_logger.LogInformation("Authentication successful. Transitioning state to Joined.");
				_currentUsername = _pendingUsername; // Confirm username
				_currentDisplayName = _pendingDisplayName; // Confirm display name

				// Set state to Joined upon OK reply for AUTH (assuming server auto-joins default)
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				_currentChannelId = "default"; // Assuming default channel name per spec

				_logger.LogInformation($"Client state set to Joined, assuming default channel '{_currentChannelId}'.");
			}
			else if (wasJoinReply)
			{
				// JOIN Success
				_logger.LogInformation($"Join channel '{_currentChannelId}' successful. Transitioning state to Joined.");
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				_logger.LogInformation($"Client state set to Joined in channel '{previousChannelIdOnJoin}'.");
			}
			else
			{
				// Received an OK REPLY when not explicitly waiting for one (or in a wrong state)
				_logger.LogWarning("Received OK REPLY in state {State} but not expecting AUTH or JOIN reply. Content: {Content}",
					_currentState, reply.MessageContent);
			}
		}
		// --NOK Reply ---
		else
		{
			string reason = reply.MessageContent;
			// Use specified "Action Failure:" format
			Console.WriteLine($"Action Failure: {reason}");

			if (wasAuthReply)
			{
				// AUTH Failure
				_logger.LogWarning("Authentication failed for pending user '{PendingUsername}'. Reason: {Reason}. Transitioning back to Start state.", _pendingUsername, reason);
				Utils.SetState(ref _currentState, ClientState.Start, _logger);
				_currentUsername = null; // Clear local credentials on failure
				_currentDisplayName = null;
				_pendingUsername = null; // Also clear pending ones
				_pendingSecret = null;
				_pendingDisplayName = null;
			}
			else if (wasJoinReply)
			{
				// JOIN Failure
				_logger.LogWarning("Join channel '{FailedChannelId}' failed. Reason: {Reason}. Transitioning back to Authenticated state.", previousChannelIdOnJoin, reason);
				Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Assume Authenticated was the state before attempting JOIN
				_currentChannelId = null; // Clear pending channel ID on failure
			}
			else
			{
				// Received a NOK REPLY when not explicitly waiting for one (or in a wrong state)
				// Logged in ParseAndHandleReply already. No state change here.
				_logger.LogWarning("Received NOK REPLY in state {State} but not expecting AUTH or JOIN reply. Content: {Content}",
					_currentState, reply.MessageContent);
			}
		}
	}

	// Parses and handles an incoming MSG packet. Displays it if in Joined state using the specified format.
	private void ParseAndHandleMsg(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// UDP MSG Format: Type(1) | MessageID(2) | DisplayName | 0 | MessageContents | 0
		_logger.LogDebug("Parsing received MSG message (ID: {MessageId}, Size: {BytesRead}) from {Sender}", messageId, bytesRead, sender);

		// Validate minimum size based on MessageSizeBytes enum
		if (bytesRead < (int)MessagesSizeBytes.Msg) // MSG header is 1+2+1 for length or 1+2+DN_len+1? Needs spec check. Assuming original enum value is header+lengths.
		{
			_logger.LogWarning("Received malformed MSG (ID: {MessageId}, too short: {Length} bytes) from {Sender}. Ignoring.", messageId, bytesRead, sender);
            // Optionally send ERR back for malformed packets
            // SendErrorAsync($"Malformed MSG packet (too short: {bytesRead} bytes).", sender).GetAwaiter().GetResult(); // Sync call
			return; // Exit handling
		}

		// --- Duplicate Message Check ---
		if (!_processedIncomingMessageIds.TryAdd(messageId, 1))
		{
			_logger.LogDebug("Received duplicate MSG (ID: {MessageId}) from {Sender}. Ignoring display.", messageId, sender);
			return; // Ignore duplicate
		}

		try
		{
			// Find null terminators for DisplayName and Content strings
			int displayNameStartIndex = 3; // After Type (1) + MessageID (2)
			int displayNameNullPos = Array.IndexOf(buffer, (byte)0, displayNameStartIndex, bytesRead - displayNameStartIndex);

			if (displayNameNullPos < displayNameStartIndex) // No null terminator found for display name
			{
				_logger.LogWarning("Malformed MSG (ID: {MessageId}): No DisplayName null terminator found. Ignoring.", messageId);
                // Optionally send ERR back
                // SendErrorAsync($"Malformed MSG packet (missing DisplayName null terminator).", sender).GetAwaiter().GetResult(); // Sync call
				return; // Exit handling
			}

			string displayName = Encoding.ASCII.GetString(buffer, displayNameStartIndex, displayNameNullPos - displayNameStartIndex);

			int contentStartIndex = displayNameNullPos + 1; // Start after the display name null terminator
			if (contentStartIndex >= bytesRead) // No content present
			{
				_logger.LogWarning("Malformed MSG (ID: {MessageId}): No message content found. Ignoring.", messageId);
                 // Optionally send ERR back
                 // SendErrorAsync($"Malformed MSG packet (missing content).", sender).GetAwaiter().GetResult(); // Sync call
				return; // Exit handling
			}

			int contentNullPos = Array.IndexOf(buffer, (byte)0, contentStartIndex, bytesRead - contentStartIndex);

			// Protocol might require content always ends with 0x00, or allow variable length up to packet end.
            // Assuming it ends with a null terminator as per original code structure.
			if (contentNullPos < contentStartIndex) // No null terminator found for content
			{
                 _logger.LogWarning("Malformed MSG (ID: {MessageId}): No Content null terminator found. Using remaining bytes as content.", messageId);
                 contentNullPos = bytesRead; // Treat rest as content
			}

			string messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, contentNullPos - contentStartIndex);

			_logger.LogTrace("Parsed MSG ID:{MessageId} From:'{DisplayName}' Content:'{Content}'", messageId, displayName, messageContent);

			// Display message only if client is in the Joined state
			if (_currentState == ClientState.Joined)
			{
				// Use specified MSG format
				Console.WriteLine($"{displayName}: {messageContent}");
			}
			else
			{
				// Received MSG message in an unexpected state
				_logger.LogWarning("Received MSG (ID:{MessageId}) from {DisplayName} while not in Joined state ({State}). Ignoring display.",
					messageId, displayName, _currentState);
				// No console output for MSG in wrong state according to strict rules. Log only.
			}
		}
		catch (ArgumentOutOfRangeException aoorex) // LogError during GetString if indices are wrong
		{
			_logger.LogError(aoorex, "Parsing error (ArgumentOutOfRange) processing MSG ID:{MessageId} from {Sender}. Check indices/lengths. BytesRead:{BytesRead}",
				messageId, sender, bytesRead);
             Console.WriteLine($"ERROR: Internal parsing error for incoming MSG message from {sender}."); // Use internal error format
             // Optionally send ERR back
             // SendErrorAsync($"Internal error parsing MSG packet.", sender).GetAwaiter().GetResult(); // Sync call
		}
		catch (Exception ex) // Catch-all for other potential parsing errors
		{
			_logger.LogError(ex, "Unexpected exception while parsing MSG message ID:{MessageId} from {Sender}", messageId, sender);
             Console.WriteLine($"ERROR: Unexpected internal error parsing incoming MSG message from {sender}."); // Use internal error format
             // Optionally send ERR back
             // SendErrorAsync($"Unexpected error parsing MSG packet.", sender).GetAwaiter().GetResult(); // Sync call
		}
	}

	// Parses and handles an incoming ERR packet. Displays it using the specified format and initiates shutdown.
	private async Task ParseAndHandleErrAsync(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// ERR Format: Type(1) | MessageID(2) | DisplayName | 0 | MessageContents | 0 (Same format as MSG)
		_logger.LogDebug("Parsing received ERR message (ID:{MessageId}, Size:{BytesRead}) from {Sender}.", messageId, bytesRead, sender);

		if (bytesRead < (int)MessagesSizeBytes.Err) // Validate minimum size
		{
			_logger.LogError("Malformed ERR (ID:{MessageId}, too short: {Length} bytes) from {Sender}. Ignoring.", messageId, bytesRead, sender);
             Console.WriteLine($"ERROR: Received malformed server ERR packet (too short: {bytesRead} bytes) from {sender}."); // Use internal error format
			return;
		}

		string displayName = "Unknown"; // Default display name if parsing fails
		string messageContent = "(Failed to parse error message)"; // Default content if parsing fails

		try
		{
			// Find null terminators for DisplayName and Content strings (same logic as MSG parsing)
			int displayNameStartIndex = 3; // After Type (1) + MessageID (2)
			int displayNameNullPos = Array.IndexOf(buffer, (byte)0, displayNameStartIndex, bytesRead - displayNameStartIndex);

			if (displayNameNullPos < displayNameStartIndex) // No null terminator found for display name
			{
				_logger.LogWarning("Malformed ERR (ID: {MessageId}): No DisplayName null terminator found. Using 'Unknown' display name.", messageId);
			}
			else
			{
				displayName = Encoding.ASCII.GetString(buffer, displayNameStartIndex, displayNameNullPos - displayNameStartIndex);
			}

			int contentStartIndex = (displayNameNullPos >= displayNameStartIndex ? displayNameNullPos + 1 : displayNameStartIndex); // Start after DN or after ID if DN missing
			if (contentStartIndex < bytesRead) // Only look for content if there are bytes after the display name/header
			{
                int contentNullPos = Array.IndexOf(buffer, (byte)0, contentStartIndex, bytesRead - contentStartIndex);
                if (contentNullPos < contentStartIndex) // No null terminator for content
                {
                     _logger.LogWarning("Malformed ERR (ID: {MessageId}): No Content null terminator found. Using remaining bytes as content.", messageId);
                     contentNullPos = bytesRead; // Use remaining bytes
                }
                messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, contentNullPos - contentStartIndex);
			}
		}
		catch (Exception ex) // Catch-all for parsing errors
		{
			_logger.LogError(ex, "Exception while parsing ERR message ID:{MessageId}. Using defaults.", messageId);
             Console.WriteLine($"ERROR: Internal parsing error for incoming ERR message from {sender}."); // Use internal error format
		}

		_logger.LogError("ERR received from {DisplayName} ({Sender}): {Content}. Initiating shutdown.", displayName, sender, messageContent);
		Console.WriteLine($"ERROR FROM {displayName}: {messageContent}");

		// Initiate shutdown immediately (don't send BYE after receiving ERR)
		await InitiateShutdownAsync($"Received ERR from server ({displayName}).", isClientInitiatedEof: false); // Defined in main partial
	}

	// Parses and handles an incoming BYE packet. Displays it and initiates shutdown.
	private async Task ParseAndHandleByeAsync(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// BYE Format: Type(1) | MessageID(2) | DisplayName | 0
		_logger.LogInformation("Received BYE message (ID:{MessageId}) from {Sender}.", messageId, sender);

		if (bytesRead < (int)MessagesSizeBytes.Bye) // Validate minimum size
		{
			_logger.LogWarning("Received malformed BYE (ID:{MessageId}, too short: {Length} bytes) from {Sender}. Ignoring.", messageId, bytesRead, sender);
            Console.WriteLine($"ERROR: Received malformed server BYE packet (too short: {bytesRead} bytes) from {sender}."); // Use internal error format
			return; // Exit handling
		}

		string displayName = "Unknown"; // Default display name if parsing fails

		try
		{
			// Find null terminator for DisplayName string
			int displayNameStartIndex = 3; // After Type (1) + MessageID (2)
			int displayNameNullPos = Array.IndexOf(buffer, (byte)0, displayNameStartIndex, bytesRead - displayNameStartIndex);

			if (displayNameNullPos < displayNameStartIndex) // No null terminator found for display name
			{
				_logger.LogWarning("Malformed BYE (ID: {MessageId}): No DisplayName null terminator found. Using 'Unknown' display name.", messageId);
			}
			else
			{
				displayName = Encoding.ASCII.GetString(buffer, displayNameStartIndex, displayNameNullPos - displayNameStartIndex);
			}
            // Check for extra bytes after the display name null terminator if protocol is strict
            // if (displayNameNullPos + 1 != bytesRead) { ... malformed ... }
		}
		catch (Exception ex) // Catch-all for parsing errors
		{
			_logger.LogError(ex, "Exception while parsing BYE message ID:{MessageId}. Using default name.", messageId);
            Console.WriteLine($"ERROR: Internal parsing error for incoming BYE message from {sender}."); // Use internal error format
		}

		// Log and Display shutdown message
		_logger.LogInformation("BYE received from {DisplayName} ({Sender}). Initiating shutdown.", displayName, sender);
		Console.WriteLine($"ERROR: Server initiated shutdown from {displayName}.");

		await InitiateShutdownAsync($"Received BYE from server ({displayName}).", isClientInitiatedEof: false); // Defined in main partial
	}
}
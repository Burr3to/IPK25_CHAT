namespace IPK25_CHAT.Udp;

public partial class UdpChatClient
{
	private async Task ReceiveMessagesUdpAsync(CancellationToken cancellationToken)
	{
		_logger.LogDebug("Starting message receiver loop...");
		var buffer = new byte[65535]; 
		EndPoint senderEndPoint = new IPEndPoint(IPAddress.Any, 0); 

		while (!cancellationToken.IsCancellationRequested && _socket != null && _currentState != ClientState.End)
		{
			try
			{
				var receiveResult = await _socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, senderEndPoint);
				int bytesRead = receiveResult.ReceivedBytes;
				var currentSender = (IPEndPoint)receiveResult.RemoteEndPoint; // Get sender IP & Port

				if (bytesRead < 3) // Minimum header size
				{
					_logger.LogWarning("Received runt datagram ({BytesRead} bytes) from {Sender}. Ignoring.", bytesRead, currentSender);
					continue;
				}

				byte messageType = buffer[0];
				ushort messageId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 1));

				_logger.LogTrace("Received {BytesRead} bytes from {Sender}. Type: 0x{Type:X2}, ID: {MessageId}", bytesRead, currentSender, messageType, messageId);

				HandleDynamicPortUpdate(currentSender);

				if (messageType == (byte)Utils.MessageType.ConfirmType)
				{
					HandleIncomingConfirm(messageId, bytesRead, currentSender);
					continue;
				}

				// Send CONFIRM back for other message types
				await SendConfirmationAsync(messageId, currentSender, CancellationToken.None); 

				// Process Specific Message Types
				switch (messageType)
				{
					case (byte)Utils.MessageType.ReplyType: 
						ParseAndHandleReply(buffer, bytesRead, messageId, currentSender);
						break;
					case (byte)Utils.MessageType.MsgType: 
						ParseAndHandleMsg(buffer, bytesRead, messageId, currentSender);
						break;
					case (byte)Utils.MessageType.PingType: 
						_logger.LogDebug("Received PING (ID: {MessageId}) from {Sender}.", messageId, currentSender);
						break;
					case (byte)Utils.MessageType.ErrType: 
						await ParseAndHandleErrAsync(buffer, bytesRead, messageId, currentSender); 
						break;
					case (byte)Utils.MessageType.ByeType: 
						await ParseAndHandleByeAsync(buffer, bytesRead, messageId, currentSender); 
						break;
					default:
						_logger.LogWarning("Received unknown/unhandled message Type 0x{Type:X2} ({BytesRead} bytes) from {Sender}. Ignoring content.", messageType, bytesRead,
							currentSender);
						break;
				}
			}
			catch (OperationCanceledException)
			{
				_logger.LogInformation("Message receiver loop cancelled.");
				break; 
			}
			catch (SocketException se)
			{
				if (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
				{
					Console.WriteLine("\nError: Connection to server appears to be lost.");
					_logger.LogError("Assuming server connection lost due to SocketException {ErrorCode}. Initiating shutdown.", se.SocketErrorCode);
					await InitiateShutdownAsync("Server connection lost (SocketException).", false);
					break; 
				}
			}
			catch (ObjectDisposedException)
			{
				_logger.LogWarning("Receive attempted on disposed socket. Exiting loop.");
				break; 
			}
			catch (Exception ex) // Catch-all for unexpected errors
			{
				if (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
				{
					_logger.LogError(ex, "Unhandled exception in receiver loop.");
					Console.WriteLine($"\nCritical error receiving data: {ex.Message}");
					await InitiateShutdownAsync($"Critical error in receive loop: {ex.Message}", false);
				}
				else
				{
					_logger.LogInformation("Exception in receiver loop, but cancellation/shutdown was in progress: {ExceptionMessage}", ex.Message);
				}

				break;
			}
		} 

		_logger.LogDebug("Exiting message receiver loop.");
	}

	private void HandleDynamicPortUpdate(IPEndPoint currentSender)
	{
		if (_currentState == ClientState.Authenticating && !_currentServerEndPoint.Equals(currentSender))
		{
			if (_currentServerEndPoint.Equals(_initialServerEndPoint))
			{
				_logger.LogInformation("Detected server endpoint switch from {OldEp} to {NewEp}. Updating current target.", _currentServerEndPoint, currentSender);
				_currentServerEndPoint = currentSender; // Update the target for future sends!
			}
			else
			{
				_logger.LogWarning("Received message from endpoint {Sender} while authenticating, but already switched to {CurrentEp}. Ignoring potential port switch.",
					currentSender, _currentServerEndPoint);
			}
		}
	}

	private void HandleIncomingConfirm(ushort confirmRefId, int bytesRead, IPEndPoint sender)
	{
		if (bytesRead == (byte)Utils.MessagesSizeBytes.Confirm) // Correct size for CONFIRM
		{
			_logger.LogDebug("Received CONFIRM for MessageID: {RefMessageId} from {Sender}.", confirmRefId, sender);

			if (_pendingConfirms.TryRemove(confirmRefId, out var confirmTcs)) // Remove *and* get
			{
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
			_logger.LogWarning("Received malformed CONFIRM (size {BytesRead} != 3) from {Sender}. Ignoring.", bytesRead, sender);
		}
	}

	private void ParseAndHandleReply(byte[] buffer, int bytesRead, ushort receivedMessageId, IPEndPoint sender)
	{
		_logger.LogDebug("Parsing received REPLY message (ID: {MessageId}, Size: {BytesRead}) from {Sender}", receivedMessageId, bytesRead, sender);

		if (bytesRead < (int)Utils.MessagesSizeBytes.Reply)
		{
			_logger.LogWarning("Received malformed REPLY (ID: {MessageId}, too short: {Length} bytes) from {Sender}. Ignoring.", receivedMessageId, bytesRead, sender);
			return;
		}

		try
		{
			byte resultByte = buffer[3];
			bool isOkReply = (resultByte == 1); 

			ushort refMessageId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 4));

			int contentStartIndex = 6;
			int nullTerminatorIndex = -1;
			for (int i = contentStartIndex; i < bytesRead; i++)
			{
				if (buffer[i] == 0)
				{
					nullTerminatorIndex = i;
					break;
				}
			}

			if (nullTerminatorIndex == -1)
			{
				_logger.LogWarning("Received malformed REPLY (ID: {MessageId}, no null terminator for content) from {Sender}. Ignoring.", receivedMessageId, sender);
				return;
			}

			// Extract the content string using ASCII encoding
			int contentLength = nullTerminatorIndex - contentStartIndex;
			string messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, contentLength);

			// --- Create Parsed Message Object ---
			var parsedReply = new Utils.ParsedServerMessage
			{
				// UDP specific fields
				UdpMessageType = (byte)Utils.MessageType.ReplyType, 
				MessageId = receivedMessageId, 
				Sender = sender,
				ReplyResult = isOkReply, 
				RefMessageId = refMessageId, 
				MessageContent = messageContent, 

				Type = Utils.ServerMessageType.Reply,
				IsOkReply = isOkReply,
				Content = messageContent
			};

			_logger.LogInformation("Parsed REPLY ID:{MessageId} for ReqID:{RefMessageId} from {Sender}. Result:{Result}, Content:'{Content}'",
				parsedReply.MessageId, parsedReply.RefMessageId, parsedReply.Sender, parsedReply.ReplyResult.Value ? "OK" : "NOK", parsedReply.MessageContent);

			// --- Signal the Waiting Task ---
			if (parsedReply.RefMessageId.HasValue && _pendingReplies.TryRemove(parsedReply.RefMessageId.Value, out var replyTcs)) // Remove *and* get
			{
				bool signaled = replyTcs.TrySetResult(parsedReply);
				_logger.LogDebug("Signaled pending reply handler for original request ID {RefMessageId}. Success: {Signaled}", parsedReply.RefMessageId.Value, signaled);
			}
			else
			{
				// No corresponding TaskCompletionSource was found.
				_logger.LogWarning("Received REPLY for unknown, timed-out, or already completed request ID: {RefMessageId}. Discarding.", parsedReply.RefMessageId);
			}
		}
		catch (ArgumentOutOfRangeException aoorex) // Error during BitConverter or GetString if indices are wrong
		{
			_logger.LogError(aoorex, "Parsing error (ArgumentOutOfRange) processing REPLY ID:{MessageId} from {Sender}. Check indices/lengths. BytesRead:{BytesRead}",
				receivedMessageId, sender, bytesRead);
		}
		catch (Exception ex) // Catch other potential parsing errors
		{
			_logger.LogError(ex, "Unexpected exception while parsing REPLY message ID:{MessageId} from {Sender}", receivedMessageId, sender);
		}
	}

	private void ProcessFunctionalReply(Utils.ParsedServerMessage reply)
	{
		// Infer the operation type based on the state we *expect* to be in
		ClientState stateWhenRequestSent;

		if (_currentState == ClientState.Authenticating)
			stateWhenRequestSent = ClientState.Authenticating;
		else if (_currentState == ClientState.Joining)
			stateWhenRequestSent = ClientState.Joining;
		else
		{

			_logger.LogWarning(
				"ProcessFunctionalReply called but current state is {CurrentState} (Expected Authenticating or Joining). This might be a late or unexpected reply. Ignoring reply ID:{MessageId} for RefID:{RefMessageId}.",
				_currentState, reply?.MessageId, reply?.RefMessageId);
			_pendingUsername = null;
			_pendingSecret = null;
			_pendingDisplayName = null;
			return;
		}

		_logger.LogDebug("Processing functional reply for request sent in state {StateWhenRequestSent}. Reply success: {IsOkReply}",
			stateWhenRequestSent, reply.ReplyResult ?? false); // Treat null result as failure

		if (reply.ReplyResult == true) // Check the bool? field
		{
			Console.WriteLine($"[{reply.Sender}]: {reply.MessageContent}");

			if (stateWhenRequestSent == ClientState.Authenticating)
			{
				// --- AUTH Success ---
				_currentUsername = _pendingUsername;
				_currentDisplayName = _pendingDisplayName;
				_logger.LogInformation("Authentication successful! User:'{Username}', DisplayName:'{DisplayName}'.", _currentUsername, _currentDisplayName);

				Utils.SetState(ref _currentState, ClientState.Joined, _logger); // Move to Joined state
				_currentChannelId = "default"; // Assume default channel per spec

				Console.WriteLine($"--> Successfully joined default channel as '{_currentDisplayName}'.");
				Console.WriteLine("--> You can now send messages or use /join, /rename, /bye, /help.");
			}
			else 
			{
				// JOIN Success
				_logger.LogInformation("Join channel '{ChannelId}' successful.", _currentChannelId);

				Utils.SetState(ref _currentState, ClientState.Joined, _logger); 

				Console.WriteLine($"--> Successfully joined channel '{_currentChannelId}'.");
			}
		}
		// --NOK Reply ---
		else
		{
			string reason = reply.MessageContent;
			Console.WriteLine($"Error: Operation failed. Server reply from [{reply.Sender}]: {reason}");

			if (stateWhenRequestSent == ClientState.Authenticating)
			{
				// --- AUTH Failure ---
				_logger.LogError("Authentication failed for pending user '{PendingUsername}'. Reason: {Reason}", _pendingUsername, reason);

				Utils.SetState(ref _currentState, ClientState.Start, _logger); // Revert state to Start

				Console.WriteLine("--> Authentication failed. Please check credentials and try /auth again.");
			}
			else 
			{
				// --- JOIN Failure ---
				_logger.LogError("Join channel '{FailedChannelId}' failed. Reason: {Reason}", _currentChannelId, reason);
				Utils.SetState(ref _currentState, ClientState.Joined, _logger); // Revert state from Joining back to Joined
				Console.WriteLine($"--> Failed to join '{_currentChannelId}'. Still in previous channel/state.");
			}
		}

		// --- Cleanup Pending Credentials (only if processing an AUTH reply) ---
		if (stateWhenRequestSent == ClientState.Authenticating)
		{
			_pendingUsername = null;
			_pendingSecret = null;
			_pendingDisplayName = null;
			_logger.LogTrace("Cleared pending authentication credentials after processing AUTH reply.");
		}
	}

	private void ParseAndHandleMsg(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// MSG Format: 0x04 | MessageID(2) | DisplayName | 0 | MessageContents | 0
		if (bytesRead < (byte)Utils.MessagesSizeBytes.Msg)
		{
			_logger.LogWarning("Received malformed MSG (too short: {Length} bytes) from {Sender}", bytesRead, sender);
			return;
		}

		try
		{
			int null1Idx = Array.IndexOf(buffer, (byte)0, 3, bytesRead - 3);
			if (null1Idx <= 3)
			{
				_logger.LogWarning("Malformed MSG: No DisplayName or terminator found early. ID:{MessageId}", messageId);
				return;
			}

			string displayName = Encoding.ASCII.GetString(buffer, 3, null1Idx - 3);

			int contentStartIndex = null1Idx + 1;
			if (contentStartIndex >= bytesRead - 1)
			{
				_logger.LogWarning("Malformed MSG: No Content or terminator. ID:{MessageId}", messageId);
				return;
			} 

			int null2Idx = Array.IndexOf(buffer, (byte)0, contentStartIndex, bytesRead - contentStartIndex);
			if (null2Idx < contentStartIndex)
			{
				_logger.LogWarning("Malformed MSG: No Content terminator found. ID:{MessageId}", messageId);
				return;
			}

			string messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, null2Idx - contentStartIndex);

			_logger.LogTrace("Parsed MSG ID:{MessageId} From:'{DisplayName}' Content:'{Content}'", messageId, displayName, messageContent);

			if (_currentState == ClientState.Joined)
				Console.Write($"\r[{displayName}]: {messageContent}{Environment.NewLine}> ");
			else
				_logger.LogWarning("Received MSG (ID:{MessageId}) while not in Joined state ({State}). Ignoring display.", messageId, _currentState);
			
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse/handle MSG ID:{MessageId} from {Sender}", messageId, sender);
		}
	}

	private async Task ParseAndHandleErrAsync(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// ERR Format: 0xFE | MessageID(2) | DisplayName | 0 | MessageContents | 0 (Same as MSG)
		_logger.LogWarning("Received ERR message (ID:{MessageId}) from {Sender}.", messageId, sender);
		if (bytesRead < 6)
		{
			_logger.LogError("Malformed ERR (too short: {Length} bytes) from {Sender}", bytesRead, sender); 
		}

		string displayName = "Unknown"; 
		string messageContent = "(Failed to parse error message)";

		try 
		{
			int null1Idx = Array.IndexOf(buffer, (byte)0, 3, bytesRead - 3);
			if (null1Idx > 3)
			{
				displayName = Encoding.ASCII.GetString(buffer, 3, null1Idx - 3);
				int contentStartIndex = null1Idx + 1;
				if (contentStartIndex < bytesRead - 1)
				{
					int null2Idx = Array.IndexOf(buffer, (byte)0, contentStartIndex, bytesRead - contentStartIndex);
					if (null2Idx >= contentStartIndex)
					{
						// Parsed content
						messageContent = Encoding.ASCII.GetString(buffer, contentStartIndex, null2Idx - contentStartIndex);
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception while parsing ERR message ID:{MessageId}. Using defaults.", messageId);
		}

		_logger.LogError("ERR received from {DisplayName} ({Sender}): {Content}. Initiating shutdown.", displayName, sender, messageContent);
		Console.WriteLine($"\n*** Server ERROR from [{displayName}]: {messageContent} ***");
		Console.WriteLine("*** Connection terminated by server error. ***");

		await InitiateShutdownAsync($"Received ERR from server ({displayName})", false);
	}

	private async Task ParseAndHandleByeAsync(byte[] buffer, int bytesRead, ushort messageId, IPEndPoint sender)
	{
		// BYE Format: 0xFF | MessageID(2) | DisplayName | 0
		_logger.LogInformation("Received BYE message (ID:{MessageId}) from {Sender}.", messageId, sender);
		if (bytesRead < 5)
		{
			_logger.LogWarning("Received malformed BYE (too short: {Length} bytes) from {Sender}", bytesRead, sender); 
		}

		string displayName = "Unknown"; 

		try 
		{
			int null1Idx = Array.IndexOf(buffer, (byte)0, 3, bytesRead - 3);
			if (null1Idx > 3)
			{
				displayName = Encoding.ASCII.GetString(buffer, 3, null1Idx - 3);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception while parsing BYE message ID:{MessageId}. Using default name.", messageId);
		}

		// Log and Display
		_logger.LogInformation("BYE received from {DisplayName} ({Sender}). Initiating shutdown.", displayName, sender);
		Console.WriteLine($"\n--- Server [{displayName}] has disconnected. ---");

		// Initiate shutdown immediately (don't send BYE after receiving BYE)
		await InitiateShutdownAsync($"Received BYE from server ({displayName})", false);
	}

	private async Task SendConfirmationAsync(ushort receivedMessageId, IPEndPoint targetEndPoint, CancellationToken cancellationToken)
	{
		_logger.LogTrace("Preparing to send CONFIRM for received MessageID {ReceivedMessageId} to {TargetEndPoint}", receivedMessageId, targetEndPoint);

		byte[] confirmBytes = UdpMessageFormat.FormatConfirmManually(receivedMessageId);

		if (confirmBytes == null)
		{
			_logger.LogError("Failed to format CONFIRM message for received ID {ReceivedMessageId}.", receivedMessageId);
			return;
		}

		if (_socket == null || _currentState == ClientState.End) 
		{
			_logger.LogWarning("Cannot send CONFIRM for received ID {ReceivedMessageId}, socket is null or client shutting down.", receivedMessageId);
			return;
		}

		try
		{
			await _socket.SendToAsync(confirmBytes, SocketFlags.None, targetEndPoint);
			_logger.LogDebug("Sent CONFIRM for received ID {ReceivedMessageId} to {TargetEndPoint}", receivedMessageId, targetEndPoint);
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Attempted to send CONFIRM for {ReceivedMessageId} on a disposed socket.", receivedMessageId);
		}
		catch (SocketException se)
		{
			_logger.LogWarning(se, "SocketException (Code:{SocketErrorCode}) while sending CONFIRM for {ReceivedMessageId} to {TargetEndPoint}. Confirm likely not sent.",
				se.SocketErrorCode, receivedMessageId, targetEndPoint);
		}
		catch (Exception ex) 
		{
			_logger.LogError(ex, "Unexpected exception while sending CONFIRM for {ReceivedMessageId} to {TargetEndPoint}.", receivedMessageId, targetEndPoint);
		}
	}
}
namespace IPK25_CHAT.Udp;

public partial class UdpChatClient
{
	private readonly ILogger<UdpChatClient> _logger;
	private readonly Messages _messageParser;

	private string _serverHost;
	private int _port;
	private Socket _socket;


	private ClientState _currentState;
	private CancellationTokenSource _cts;

	private IPEndPoint _initialServerEndPoint; // Where to send AUTH
	private IPEndPoint _currentServerEndPoint; // Where to send after

	private ushort _nextMessageId = 0;
	private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pendingConfirms = new();
	private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Utils.ParsedServerMessage>> _pendingReplies = new();

	// temporaril
	private string _currentDisplayName;
	private string _currentUsername;
	private string _currentChannelId;
	private string _pendingUsername;
	private string _pendingSecret;
	private string _pendingDisplayName;

	private const int ReplyTimeoutMilliseconds = 250;
	private const int MaxRetries = 3; // 1 initial send + 3 retries = 4 total attempts

	public UdpChatClient(ILogger<UdpChatClient> logger, Messages messageParser)
	{
		_logger = logger;
		_messageParser = messageParser;
		_currentState = ClientState.Start;
		_cts = new CancellationTokenSource();

		Console.CancelKeyPress += async (sender, e) =>
		{
			e.Cancel = true;
			_logger.LogInformation("Ctrl+C detected. Initiating graceful shutdown...");
			try
			{
				//await InitiateShutdownAsync("User initiated shutdown (Ctrl+C).");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Exception occurred during InitiateShutdownAsync called from CancelKeyPress. Shutdown process may be incomplete.");
			}
		};
	}

	public async Task StartClientAsync(ArgumentParser.Options options)
	{
		_serverHost = options.Server;
		_port = options.Port;

		Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
		IPAddress serverIpAddress = Utils.GetFirstIPv4Address(_serverHost);

		try
		{
			_logger.LogInformation("Parsing server address: {ServerHost} as IP: {IPAddress}", _serverHost, serverIpAddress);
			_logger.LogInformation("Connecting to {ServerAddress}:{Port} via TCP...", serverIpAddress, _port);

			_socket = new Socket(serverIpAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
			_socket.Bind(new IPEndPoint(IPAddress.Any, 0));

			var localEndPoint = new IPEndPoint(serverIpAddress, _port);
			_socket.Bind(localEndPoint);
			Utils.SetState(ref _currentState, ClientState.Start, _logger); // Correct initial state
			_logger.LogInformation("UDP Socket bound locally to: {LocalEndPoint}", _socket.LocalEndPoint);
			_logger.LogInformation("UDP Client ready. Please authenticate using /auth <Username> <Secret> <DisplayName>");

		
			var receiveTask = ReceiveMessagesUdpAsync(_cts.Token); 
			var sendTask = HandleUserInputUdpAsync(_cts.Token);

			await Task.WhenAny(receiveTask, sendTask);
		}
		catch (Exception e)
		{
			Console.WriteLine(e);
			throw;
		}
		finally
		{
			OwnDispose();
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			_logger.LogInformation("Client StartClientAsync finished, resources disposed.");
		}
	}

	private async Task HandleUserInputUdpAsync(CancellationToken cancellationToken) 
	{
		_logger.LogDebug("Starting user input handler loop...");
		try
		{
			while (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
			{
				Console.Write("> "); // Prompt user
				string input = await Task.Run(() => Console.ReadLine(), cancellationToken); // ReadLine on background thread

				if (input == null || cancellationToken.IsCancellationRequested) // Ctrl+D or cancellation
				{
					_logger.LogInformation("End of input detected or cancellation requested. Initiating graceful shutdown...");
					if (!_cts.IsCancellationRequested)
						await InitiateShutdownAsync("User initiated shutdown (EOF/Ctrl+D/Cancel).", true);
					break;
				}

				if (string.IsNullOrWhiteSpace(input)) continue;

				var parsed = _messageParser.ParseUserInput(input);

				if (parsed.Type == Messages.CommandParseResultType.Unknown)
					continue;

				bool canProceed = true;
				switch (_currentState)
				{
					case ClientState.Start:
					case ClientState.Connected:
						if (parsed.Type != Messages.CommandParseResultType.Auth && parsed.Type != Messages.CommandParseResultType.Help)
						{
							Console.WriteLine("ERROR: Must authenticate first. Use /auth <Username> <Secret> <DisplayName>");
							canProceed = false;
						}

						break;

					case ClientState.Authenticating:
						if (parsed.Type != Messages.CommandParseResultType.Help) // Allow only help while authenticating
						{
							Console.WriteLine("ERROR: Please wait for authentication to complete.");
							canProceed = false;
						}

						break;

					case ClientState.Joining: // zbytocne?
						if (parsed.Type != Messages.CommandParseResultType.Help) // Allow only help while joining
						{
							Console.WriteLine("ERROR: Please wait for join operation to complete.");
							canProceed = false;
						}

						break;

					case ClientState.Joined:
						if (parsed.Type == Messages.CommandParseResultType.Auth)
						{
							Console.WriteLine("ERROR: Already authenticated.");
							canProceed = false;
						}

						// Allow ChatMessage, Join, Rename, Help in Joined state
						break;

					case ClientState.End:
						_logger.LogDebug("Input received while in End state, ignoring.");
						canProceed = false;
						break;

					default: 
						_logger.LogWarning("Input received in unexpected state: {State}", _currentState);
						canProceed = false;
						break;
				}

				if (!canProceed)
				{
					continue; // Skip processing if state validation failed
				}

				await ProcessParsedCommandAsync(parsed, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation("User input handler loop cancelled.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error in user input handler loop.");
			if (!_cts.IsCancellationRequested)
				await InitiateShutdownAsync($"User input loop ERROR: {ex.Message}", false);
		}
		finally
		{
			_logger.LogDebug("Exiting user input handler loop.");
			if (!_cts.IsCancellationRequested)
			{
				_logger.LogWarning("User input loop exiting unexpectedly. Triggering main cancellation.");
				_cts.Cancel(); // Trigger cancellation for the receive loop
			}
		}
	}

	private async Task ProcessParsedCommandAsync(Messages.ParsedUserInput parsed, CancellationToken cancellationToken)
	{
		switch (parsed.Type)
		{
			case Messages.CommandParseResultType.Auth:
				_pendingUsername = parsed.Username;
				_pendingSecret = parsed.Secret;
				_pendingDisplayName = parsed.DisplayName;

				Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
				Console.WriteLine($"Attempting authentication as '{_pendingDisplayName}'...");
				await SendAuthRequestAsync(_pendingUsername, _pendingDisplayName, _pendingSecret, cancellationToken);

				break;

			case Messages.CommandParseResultType.Join:
				if (string.IsNullOrEmpty(_currentDisplayName))
				{
					_logger.LogWarning("Attempted /join without a current display name (auth likely failed or pending).");
					Console.WriteLine("ERROR: Cannot join - Authentication required and must be successful first.");
					return;
				}

				Utils.SetState(ref _currentState, ClientState.Joining, _logger);
				_currentChannelId = parsed.ChannelId;
				Console.WriteLine($"Attempting to join channel '{parsed.ChannelId}'...");

				await SendJoinRequestAsync(parsed.ChannelId, _currentDisplayName, cancellationToken);
				break;

			case Messages.CommandParseResultType.Rename:
				_currentDisplayName = parsed.DisplayName;
				Console.WriteLine($"Display name changed locally to: {_currentDisplayName}");
				_logger.LogInformation("Local display name changed to {DisplayName}", _currentDisplayName);
				break;

			case Messages.CommandParseResultType.Help:
				Utils.PrintHelp();
				break;

			case Messages.CommandParseResultType.ChatMessage:
				if (string.IsNullOrEmpty(_currentDisplayName))
				{
					_logger.LogWarning("Attempted to send message without a current display name (auth likely failed or pending).");
					Console.WriteLine("ERROR: Cannot send message - Authentication required and must be successful first.");
					return;
				}

				await SendMessageRequestAsync(_currentDisplayName, parsed.OriginalInput, cancellationToken);
				break;

		}
	}


	private async Task SendMessageRequestAsync(string displayName, string messageContent, CancellationToken cancellationToken)
	{
		if (_currentState != ClientState.Joined)
		{
			_logger.LogWarning("SendMessageRequestAsync called when not in Joined state ({State}). Message not sent.", _currentState);
			Console.WriteLine("ERROR: Cannot send message, not currently joined to a channel.");
			return;
		}

		_logger.LogDebug("Preparing to send MSG: From='{DisplayName}', Content='{Content}'", displayName, messageContent);

		ushort messageId = _nextMessageId++;

		byte[] msgBytes = UdpMessageFormat.FormatMsgManually(messageId, displayName, messageContent);
		if (msgBytes == null)
		{
			_logger.LogError("Critical: Failed to format MSG message (ID: {MessageId}).", messageId);
			Console.WriteLine("Error: Internal error formatting chat message.");
			return;
		}

		_logger.LogDebug("Sending MSG (ID: {MessageId}) to {EndPoint} and awaiting CONFIRM.", messageId, _currentServerEndPoint);
		bool confirmed = await SendReliableUdpMessageAsync(messageId, msgBytes, _currentServerEndPoint, cancellationToken);

		if (!confirmed)
		{
			_logger.LogError("MSG message (ID: {MessageId}) was not confirmed by the server after retries.", messageId);
			Console.WriteLine("Warning: Server did not confirm receipt of your message (ID: {MessageId}). It might not have been delivered.", messageId);
		}
		else
		{
			_logger.LogInformation("MSG message (ID: {MessageId}) successfully confirmed by server.", messageId);
		}
	}

	private async Task<bool> SendReliableUdpMessageAsync(ushort messageId, byte[] messageBytes, IPEndPoint targetEndPoint, CancellationToken cancellationToken)
	{
		var confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		if (!_pendingConfirms.TryAdd(messageId, confirmTcs))
		{
			_logger.LogWarning("Concurrency issue: Failed to add pending confirm for MessageID: {MessageId}. Already waiting?", messageId);
			return false;
		}

		_logger.LogDebug("Attempting to send message ID: {MessageId} to {TargetEndPoint}, awaiting CONFIRM.", messageId, targetEndPoint);

		try
		{
			// Retry Loop: 4 totoal
			for (int attempt = 0; attempt <= MaxRetries; attempt++)
			{
				// Check for cancellation before each attempt
				if (cancellationToken.IsCancellationRequested || _currentState == ClientState.End)
				{
					_logger.LogInformation("Reliable send cancelled before attempt {Attempt} for MessageID: {MessageId}.", attempt, messageId);
					return false; // Cancelled
				}

				try
				{
					if (_socket == null)
					{
						_logger.LogWarning("Cannot send MessageID {MessageId}, socket is null.", messageId);
						return false;
					}

					var bytesSent = await _socket.SendToAsync(messageBytes, SocketFlags.None, targetEndPoint);

					_logger.LogTrace("Attempt {AttemptNum}/{TotalAttempts}: Sent message ID: {MessageId} ({NumBytes} bytes) to {TargetEndPoint}.",
						attempt + 1, MaxRetries + 1, messageId, bytesSent, targetEndPoint);
				}
				catch (SocketException se)
				{
					_logger.LogError(se, "SocketException on SendToAsync for MessageID: {MessageId}, Attempt: {Attempt}. Retrying...", messageId, attempt + 1);
					await Task.Delay(100, cancellationToken);
					continue;
				}
				catch (ObjectDisposedException)
				{
					_logger.LogWarning("Attempted send on disposed socket for MessageID: {MessageId}. Aborting.", messageId);
					return false;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_logger.LogError(ex, "Unexpected exception during SendToAsync for MessageID: {MessageId}, Attempt: {Attempt}. Aborting send.", messageId, attempt + 1);
					return false;
				}

				try
				{
					using var timeoutCts = new CancellationTokenSource(ReplyTimeoutMilliseconds);
					using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

					var completedTask = await Task.WhenAny(confirmTcs.Task, Task.Delay(-1, linkedCts.Token));
					if (completedTask == confirmTcs.Task)
					{
						await confirmTcs.Task;
						_logger.LogDebug("CONFIRM received for MessageID: {MessageId}.", messageId);
						return true; 
					}
					else
					{
						_logger.LogWarning("Timeout waiting for CONFIRM for MessageID: {MessageId}, Attempt: {Attempt}/{MaxAttempts}.", messageId, attempt + 1, MaxRetries + 1);
					}
				}
				catch (OperationCanceledException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						_logger.LogInformation("Reliable send cancelled by main token while waiting for CONFIRM for MessageID: {MessageId}.", messageId);
						return false; 
					}
					else
					{
						_logger.LogWarning("Timeout (via cancellation) waiting for CONFIRM for MessageID: {MessageId}, Attempt: {Attempt}/{MaxAttempts}.", messageId, attempt + 1,
							MaxRetries + 1);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Exception while waiting for CONFIRM Task for MessageID: {MessageId}. Aborting.", messageId);
					return false;
				}
			}

			_logger.LogError("Message ID: {MessageId} failed to get CONFIRM after {NumAttempts} attempts.", messageId, MaxRetries + 1);
			return false;
		}
		finally
		{
			_pendingConfirms.TryRemove(messageId, out _);
			_logger.LogTrace("Removed pending confirm entry for MessageID: {MessageId}", messageId);
		}
	}

	private async Task SendRequestAndWaitForReplyAsync(ushort messageId, byte[] messageBytes,IPEndPoint endPoint, CancellationToken cancellationToken, string requestDescription, ClientState failureState)
	{
		_logger.LogDebug("Sending {Description} (ID: {MessageId}) to {EndPoint} and awaiting CONFIRM.", requestDescription, messageId, endPoint);
	
		bool confirmed = await SendReliableUdpMessageAsync(messageId, messageBytes, endPoint, cancellationToken);

		if (!confirmed)
		{
			_logger.LogError("{Description} message (ID: {MessageId}) was not confirmed by the server.", requestDescription, messageId);
			Console.WriteLine($"Error: Server did not confirm {requestDescription} request. Check server status and network.");
			Utils.SetState(ref _currentState, failureState, _logger); // Revert state
			return;
		}

		_logger.LogInformation("{Description} message (ID: {MessageId}) confirmed by server. Now waiting for functional REPLY...", requestDescription, messageId);
		Console.WriteLine($"{requestDescription} request confirmed by server. Waiting for reply...");


		var replyTcs = new TaskCompletionSource<Utils.ParsedServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_pendingReplies.TryAdd(messageId, replyTcs))
		{
			_logger.LogWarning("Failed to add pending reply handler for {Description} MessageID: {MessageId}. Aborting.", requestDescription, messageId);
			Utils.SetState(ref _currentState, failureState, _logger);
			return;
		}

		var localReplyTcs = replyTcs;

		const int ReplyTimeoutMilliseconds = 5000;

		try
		{
			using var replyTimeoutCts = new CancellationTokenSource(ReplyTimeoutMilliseconds);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, replyTimeoutCts.Token);

			var completedTask = await Task.WhenAny(localReplyTcs.Task, Task.Delay(-1, linkedCts.Token));

			if (completedTask == localReplyTcs.Task)
			{
				Utils.ParsedServerMessage parsedReply = await localReplyTcs.Task;
				_logger.LogDebug("REPLY received for {Description} (ID: {MessageId}). Processing...", requestDescription, messageId);
				ProcessFunctionalReply(parsedReply); // Call the generic handler
			}
			else
			{
				_logger.LogError("Timeout waiting for REPLY to {Description} message (ID: {MessageId}).", requestDescription, messageId);
				Console.WriteLine($"Error: Server did not reply to {requestDescription} request within the timeout period.");
				Utils.SetState(ref _currentState, failureState, _logger); // Revert state
				Console.WriteLine($"--> {requestDescription} attempt failed (timeout). Reverting state.");
				if (requestDescription == "JOIN")
				{
					Console.WriteLine($"--> Still in previous channel/state after failed JOIN attempt for '{_currentChannelId}'.");
				}
			}
		}
		catch (OperationCanceledException)
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				_logger.LogError("Timeout (via cancellation) waiting for REPLY to {Description} message (ID: {MessageId}).", requestDescription, messageId);
				Console.WriteLine($"Error: Server did not reply to {requestDescription} request within the timeout period.");
				Utils.SetState(ref _currentState, failureState, _logger);
				Console.WriteLine($"--> {requestDescription} attempt failed (timeout). Reverting state.");
				if (requestDescription == "JOIN")
				{
					Console.WriteLine($"--> Still in previous channel/state after failed JOIN attempt for '{_currentChannelId}'.");
				}
			}
			else
			{
				_logger.LogInformation("Operation cancelled by main token while waiting for {Description} REPLY (ID: {MessageId}). Shutdown likely.", requestDescription,
					messageId);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred while awaiting {Description} REPLY task (ID: {MessageId}).", requestDescription, messageId);
			Console.WriteLine($"Error: An unexpected error occurred while waiting for {requestDescription} reply: {ex.Message}");
			Utils.SetState(ref _currentState, failureState, _logger); // Revert state
		}
		finally
		{
			_pendingReplies.TryRemove(messageId, out _);
			_logger.LogTrace("Cleaned up pending reply entry for {Description} message ID: {MessageId}", requestDescription, messageId);
		}
	}


	private async Task SendAuthRequestAsync(string username, string displayName, string secret, CancellationToken cancellationToken)
	{
		if (_currentState != ClientState.Authenticating)
		{
			_logger.LogWarning("SendAuthRequestAsync called unexpectedly when not in Authenticating state ({State}). Aborting.", _currentState);
			return;
		}

		_logger.LogInformation("Executing authentication request as User:'{Username}', Display:'{DisplayName}'", username, displayName);

		ushort messageId = _nextMessageId++;

		byte[] authMessageBytes = UdpMessageFormat.FormatAuthManually(messageId, username, displayName, secret);
		if (authMessageBytes == null)
		{
			_logger.LogError("Critical: Failed to format AUTH message (ID: {MessageId}).", messageId);
			Console.WriteLine("Error: Internal error formatting authentication message.");
			Utils.SetState(ref _currentState, ClientState.Start, _logger); // Revert state
			_pendingUsername = null;
			_pendingSecret = null;
			_pendingDisplayName = null;
			return;
		}

		await SendRequestAndWaitForReplyAsync(messageId, authMessageBytes, _currentServerEndPoint, cancellationToken,
			"AUTH", ClientState.Start 
		);

		_logger.LogDebug("SendAuthRequestAsync finished for ID {MessageId}.", messageId);
	}


	private async Task SendJoinRequestAsync(string channelId, string displayName, CancellationToken cancellationToken)
	{
		if (_currentState != ClientState.Joining)
		{
			_logger.LogWarning("SendJoinRequestAsync called when not in Joining state ({State}). Aborting.", _currentState);
			Utils.SetState(ref _currentState, ClientState.Joined, _logger); // Revert state immediately
			return;
		}

		_logger.LogInformation("Executing join channel request: Channel:'{ChannelId}', As:'{DisplayName}'", channelId, displayName);

		ushort messageId = _nextMessageId++;

		byte[] joinBytes = UdpMessageFormat.FormatJoinManually(messageId, channelId, displayName);
		if (joinBytes == null)
		{
			_logger.LogError("Critical: Failed to format JOIN message (ID: {MessageId}).", messageId);
			Console.WriteLine("Error: Internal error formatting join message.");
			Utils.SetState(ref _currentState, ClientState.Joined, _logger); // Revert state
			return;
		}

		await SendRequestAndWaitForReplyAsync(messageId, joinBytes, _currentServerEndPoint,
			cancellationToken, "JOIN", ClientState.Joined 
		);

		_logger.LogDebug("SendJoinRequestAsync finished for ID {MessageId}.", messageId);
	}

	private async Task InitiateShutdownAsync(string reason, bool sendByeToServer)
	{
		if (_currentState == ClientState.End || _cts == null || _cts.IsCancellationRequested)
			return;

		_logger.LogInformation("Initiating graceful shutdown. Reason: {Reason}", reason);

		var previousState = _currentState; // Prec?
		Utils.SetState(ref _currentState, ClientState.End, _logger);

		if (sendByeToServer && _socket != null && _currentServerEndPoint != null &&
		    (previousState == ClientState.Joined || previousState == ClientState.Joining || previousState == ClientState.Authenticating))
		{
			_logger.LogInformation("Attempting to send BYE message to {TargetEndPoint}...", _currentServerEndPoint);
			ushort byeId = _nextMessageId++;
			byte[] byeBytes = UdpMessageFormat.FormatByeManually(byeId, _currentDisplayName);

			if (byeBytes != null)
			{
				try
				{
					await _socket.SendToAsync(byeBytes, SocketFlags.None, _currentServerEndPoint);
					_logger.LogInformation("BYE message (ID: {ByeId}) sent (best effort).", byeId);
				}
				catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
				{
					_logger.LogWarning(ex, "Failed to send BYE message during shutdown (socket likely closed or error).");
				}
			}
			else
			{
				_logger.LogWarning("Failed to format BYE message during shutdown.");
			}
		}
		else if (sendByeToServer) // tiez prec?
		{
			_logger.LogDebug(
				"Skipping sending BYE message due to state ({PreviousState}), socket status ({SocketNotNull}), endpoint ({EndPointSet}), or display name ({DisplayNameSet})",
				previousState, _socket != null, _currentServerEndPoint != null, !string.IsNullOrEmpty(_currentDisplayName));
		}

		CancelAllPendingOperations($"Shutdown initiated: {reason}");

		try
		{
			_cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Main CancellationTokenSource was already disposed during shutdown signalling.");
		}


		_logger.LogInformation("Shutdown initiated. Waiting for tasks to complete (if applicable)...");
		Console.WriteLine("Disconnecting..."); // User feedback
	}

	private void CancelAllPendingOperations(string reason)
	{
		_logger.LogDebug("Cancelling all pending operations due to: {Reason}", reason);

		var confirmKeys = _pendingConfirms.Keys.ToList();
		foreach (var key in confirmKeys)
		{
			if (_pendingConfirms.TryRemove(key, out var tcs))
			{
				tcs.TrySetCanceled();
			}
		}

		_pendingConfirms.Clear(); 

		var replyKeys = _pendingReplies.Keys.ToList();
		foreach (var key in replyKeys)
		{
			if (_pendingReplies.TryRemove(key, out var tcs))
			{
				tcs.TrySetCanceled();
			}
		}

		_pendingReplies.Clear();
	}

	public void OwnDispose()
	{
		_logger.LogDebug("OwnDispose executing...");


		Utils.SetState(ref _currentState, ClientState.End, _logger);

		try
		{
			_cts?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during main Cts Dispose.");
		}

		_cts = null;

		_pendingConfirms?.Clear();
		_pendingReplies?.Clear();
		_logger.LogDebug("Cleared pending confirms/replies dictionaries.");


		// Dispose  Socket
		var socketToDispose = _socket;
		_socket = null; 

		if (socketToDispose != null)
		{
			_logger.LogDebug("Disposing socket...");
			try
			{
				socketToDispose.Close();
				_logger.LogDebug("Socket Close called.");
			}
			catch (Exception ex) 
			{
				_logger.LogWarning(ex, "Exception during socket Close.");
			}
			finally 
			{
				try
				{
					socketToDispose.Dispose(); // Release socket resources
					_logger.LogDebug("Socket Dispose called.");
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Exception during socket Dispose.");
				}
			}
		}
		else
		{
			_logger.LogDebug("Socket was already null during dispose.");
		}

		_logger.LogDebug("OwnDispose finished.");
	}
}
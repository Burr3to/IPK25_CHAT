namespace IPK25_CHAT;

public class TcpChatClient
{
	private readonly ILogger<TcpChatClient> _logger;
	private readonly Messages _messageParser;

	private string _serverHost;
	private int _port;

	private Socket _socket;

	private string _receiveMessage = string.Empty;
	private readonly byte[] _receiveBuffer = new byte[4096]; // Allocate once as a class field

	private ClientState _currentState;
	private string _currentDisplayName;
	private string _currentUsername;
	private string _currentChannelId;

	private CancellationTokenSource _cts;
	private CancellationTokenSource _replyTimeoutCts;
	private ManualResetEventSlim _waitForReply;


	public TcpChatClient(ILogger<TcpChatClient> logger, Messages messageParser)
	{
		_logger = logger;
		_messageParser = messageParser;
		_currentState = ClientState.Start;
		_cts = new CancellationTokenSource();
		_waitForReply = new ManualResetEventSlim(true);

		Console.CancelKeyPress += async (sender, e) =>
		{
			e.Cancel = true;
			_logger.LogInformation("Ctrl+C detected. Initiating graceful shutdown...");
			try
			{
				await InitiateShutdownAsync("User initiated shutdown (Ctrl+C).");
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

		Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Set initial state
		IPAddress serverIpAddress = Utils.GetFirstIPv4Address(_serverHost);

		try
		{
			_logger.LogInformation("Parsing server address: {ServerHost} as IP: {IPAddress}", _serverHost, serverIpAddress);

			// Create and Connect the Socket
			_logger.LogInformation("Connecting to {ServerAddress}:{Port} via TCP...", serverIpAddress, _port);
			_socket = new Socket(serverIpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

			await _socket.ConnectAsync(serverIpAddress, _port, _cts.Token);

			// Set State and Start Loops
			_logger.LogInformation("Connected to server. Please authenticate using /auth <Username> <Secret> <DisplayName>");
			Utils.SetState(ref _currentState, ClientState.Connected, _logger);

			var receiveTask = ReceiveMessagesAsync(_cts.Token);
			var sendTask = HandleUserInputAsync(_cts.Token);

			// Wait for tasks
			await Task.WhenAny(receiveTask, sendTask);

			_logger.LogInformation("Main client loop finished.");
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
			_logger.LogInformation("Client operation cancelled during connection.");
			await InitiateShutdownAsync("Operation cancelled.");
		}
		catch (SocketException ex)
		{
			_logger.LogError(ex, "Socket error during client connection or operation.");
			Utils.SetState(ref _currentState, ClientState.End, _logger); // wtf is this? 
			Environment.ExitCode = 1;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error during client startup or operation.");
			await InitiateShutdownAsync($"Unexpected startup ERROR: {ex.Message}");
			Environment.ExitCode = 1;
		}
		finally
		{
			OwnDispose();
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			_logger.LogInformation("Client StartClientAsync finished, resources disposed.");
		}
	}


	private async Task ReceiveMessagesAsync(CancellationToken token)
	{
		_logger.LogDebug("Receive loop started.");
		try
		{
			// Use Memory<byte> easier to operate with
			Memory<byte> buffer = _receiveBuffer.AsMemory();

			while (!token.IsCancellationRequested && _socket.Connected && _currentState != ClientState.End)
			{
				int bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);

				if (bytesRead == 0) // graceful disconnect
				{
					_logger.LogWarning("Server disconnected gracefully (received 0 bytes).");
					await InitiateShutdownAsync("Server closed the connection.");
					break;
				}

				// Usisng socket.recieve return value
				string receivedText = Encoding.ASCII.GetString(_receiveBuffer, 0, bytesRead);
				_receiveMessage += receivedText;

				_logger.LogDebug("Received {BytesRead} bytes. Accumulator size: {Size}", bytesRead, _receiveMessage.Length);

				await ProcessRecievedBufferAsync();
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			_logger.LogInformation("Receive loop cancelled.");
		}
		catch (SocketException ex)
		{
			// Handle cases where socket might be closed unexpectedly
			_logger.LogWarning(ex, "Socket error during receive (connection likely lost).");
			await InitiateShutdownAsync("Connection lost during receive.");
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Receive loop attempted to use a disposed socket.");
			// Shutdown likely already in progress
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error in receive loop.");
			await InitiateShutdownAsync($"Receive loop ERROR: {ex.Message}");
			Environment.ExitCode = 1;
		}

		_logger.LogDebug("Receive loop finished.");
	}

	// Helper to process messages found in _receiveMessage
	private async Task ProcessRecievedBufferAsync()
	{
		int messageEndIndex;

		while ((messageEndIndex = _receiveMessage.IndexOf(Utils.CRLF, StringComparison.Ordinal)) >= 0)
		{
			// Extracts the substring message
			string completeMessage = _receiveMessage.Substring(0, messageEndIndex);
			_logger.LogDebug("Found complete message: {Message}", completeMessage);

			await ProcessServerMessageAsync(completeMessage);

			// Remove the processed message and the CRLF
			int removeLength = messageEndIndex + Utils.CRLF.Length;
			_receiveMessage = _receiveMessage.Substring(removeLength);

			// Check state in case processing caused shutdown
			if (_currentState == ClientState.End || _cts.IsCancellationRequested)
				return;
		}

		_logger.LogDebug("Finished processing accumulator for now. Remaining size: {Size}", _receiveMessage.Length);
	}

	private async Task ProcessServerMessageAsync(string rawMessage)
	{
		var parsedMessage = Utils.ParseServerMessage(rawMessage, _logger);

		if (_currentState != ClientState.Start && _currentState != ClientState.Connecting && _currentState != ClientState.End)
		{
			if (parsedMessage.Type == Utils.ServerMessageType.Err)
			{
				_logger.LogDebug("Received ERR from {DisplayName}: {Content}", parsedMessage.DisplayName, parsedMessage.Content);
				Console.WriteLine($"ERROR FROM {parsedMessage.DisplayName}: {parsedMessage.Content}");
				await InitiateShutdownAsync($"Received ERR message from {parsedMessage.DisplayName}.", sendByeToServer: false);
				return;
			}

			if (parsedMessage.Type == Utils.ServerMessageType.Bye)
			{
				_logger.LogInformation("Received BYE from {DisplayName}. Closing connection.", parsedMessage.DisplayName);
				Console.WriteLine($"Server [{parsedMessage.DisplayName}] initiated disconnect.");
				await InitiateShutdownAsync($"Received BYE message from {parsedMessage.DisplayName}.", sendByeToServer: false);
				return;
			}
		}


		// Log only if it wasn't an ERR/BYE that triggered shutdown
		if (parsedMessage.Type != Utils.ServerMessageType.Err && parsedMessage.Type != Utils.ServerMessageType.Bye)
		{
			_logger.LogInformation("Server -> Client: {Message}", parsedMessage.OriginalMessage);
		}
		else if (_currentState == ClientState.End) // Or if already ended before processing ERR/BYE
		{
			_logger.LogDebug("Ignoring received message ({Type}) in state {State}.", parsedMessage.Type, _currentState);
			return;
		}


		switch (_currentState)
		{
			case ClientState.Authenticating:
			case ClientState.Joining:
				if (parsedMessage.Type == Utils.ServerMessageType.Reply)
				{
					HandleReply(parsedMessage);
				}
				else // Handle other unexpected types (like MSG)
				{
					_logger.LogWarning("Received unexpected message ({Type}) while waiting for REPLY.", parsedMessage.Type);
					// Optional: Consider if receiving MSG here should also be a fatal error?
					// await SendErrAndShutdownAsync($"Unexpected message ({parsedMessage.Type}) received while waiting for REPLY.");
				}

				break;

			case ClientState.Joined:
				switch (parsedMessage.Type)
				{
					case Utils.ServerMessageType.Msg:
						Console.WriteLine($"{parsedMessage.DisplayName}: {parsedMessage.Content}");
						break;
					case Utils.ServerMessageType.Reply:
						_logger.LogWarning("Received unexpected REPLY in Joined state: {OriginalMessage}", parsedMessage.OriginalMessage);
						Console.WriteLine($"ERROR FROM {parsedMessage.DisplayName}: {parsedMessage.Content}");
						break;
					default:
						_logger.LogWarning("Received message undefined/malformed {Type} in Joined state", parsedMessage.Type);
						Console.WriteLine($"ERROR: Missing or malformed ERR message.");
						await SendErrAndShutdownAsync($"Received message undefined/malformed ({parsedMessage.Type}).");
						break;
				}

				break;

			case ClientState.Connected:
				_logger.LogWarning("Received unexpected message ({Type}) in state {State} before authentication.", parsedMessage.Type, _currentState);
				// You might want to shut down here too, depending on protocol strictness.
				// await SendErrAndShutdownAsync($"Unexpected message ({parsedMessage.Type}) received before authentication.");
				break;

			case ClientState.End:
				break;

			default: // Start, Connecting
				_logger.LogWarning("Received message ({Type}) in unexpected state: {State}", parsedMessage.Type, _currentState);
				break;
		}
	}

	private void HandleReply(Utils.ParsedServerMessage reply)
	{
		_replyTimeoutCts.Cancel();
		_replyTimeoutCts.Dispose();
		_replyTimeoutCts = null;

		//Console.WriteLine($"Server Reply: {(reply.IsOkReply ? "OK" : "NOK")} - {reply.Content}");
		_logger.LogDebug($"Server Reply: {(reply.IsOkReply ? "OK" : "NOK")} - {reply.Content}");

		bool wasAuth = _currentState == ClientState.Authenticating;
		bool wasJoin = _currentState == ClientState.Joining;

		string previousChannelId = _currentChannelId; // Store before potentially changing state

		if (reply.IsOkReply)
		{
			if (wasAuth)
			{
				_logger.LogInformation("Authentication successful.");
				Utils.SetState(ref _currentState, ClientState.Joined,_logger);
				_currentChannelId = "default"; // Spec: server must join you in default channel immediately
				Console.WriteLine($"Action Success: {reply.Content}");
			}
			else if (wasJoin)
			{
				_logger.LogInformation($"Join channel {_currentChannelId} successful.");
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				Console.WriteLine($"Action Success: {reply.Content}");
			}
			else
				_logger.LogWarning("Received OK REPLY but wasn't in Authenticating or Joining state.");
		}
		else // NOK Reply
		{
			_logger.LogDebug($"Handling NOK reply. wasAuth: {wasAuth}, wasJoin: {wasJoin}, reply.Content: '{reply.Content}'");

			if (wasAuth)
			{
				_logger.LogDebug("Current state is Authenticating.");
				_logger.LogWarning("Authentication failed: {Reason}  Please check credentials and try /auth again.\"", reply.Content);
				Utils.SetState(ref _currentState, ClientState.Connected, _logger); // Go back to state where /auth is needed
				Console.WriteLine($"Action Failure: {reply.Content}");
			}
			else if (wasJoin)
			{
				_logger.LogDebug("Current state is Joining.");
				_logger.LogWarning("Join failed: {Reason}", reply.Content);
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				_currentChannelId = previousChannelId;
				Console.WriteLine($"Action Failure: {reply.Content}");
			}
			else
			{
				_logger.LogDebug("Current state is neither Authenticating nor Joining.");
				_logger.LogWarning("Received NOK REPLY but wasn't in Authenticating or Joining state.");
			}

			_logger.LogDebug("Finished handling NOK reply.");
			// _waitForReply.Set();
		}

		_waitForReply.Set();
	}

	private async Task HandleUserInputAsync(CancellationToken token)
	{
		_logger.LogDebug("User input loop started.");
		try
		{
			while (!token.IsCancellationRequested && _currentState != ClientState.End)
			{
				if (!_waitForReply.IsSet)
				{
					_logger.LogDebug("Waiting for server reply...");
					try
					{
						await Task.Run(() => _waitForReply.Wait(token), token);
					}
					catch (OperationCanceledException)
					{
						break;
					} // Exit if cancelled while waiting

					_logger.LogDebug("Wait for server reply finished.");
					if (token.IsCancellationRequested || _currentState == ClientState.End) break;
				}

				string input = await Task.Run(() => Console.ReadLine(), token); // ReadLine on background thread

				if (input == null) // Ctrl+D
				{
					_logger.LogInformation("End of input detected (Ctrl+D). Initiating graceful shutdown...");
					await InitiateShutdownAsync("User initiated shutdown (Ctrl+D).", true);
					break;
				}

				if (string.IsNullOrWhiteSpace(input)) continue;

				var parsed = _messageParser.ParseUserInput(input);

				// State validation
				if (_currentState != ClientState.Joined &&
				    (parsed.Type == Messages.CommandParseResultType.ChatMessage || parsed.Type == Messages.CommandParseResultType.Join))
				{
					Console.WriteLine("ERROR: Cannot send messages or join channels until authenticated and joined.");
					continue;
				}

				if (_currentState != ClientState.Connected && _currentState != ClientState.Joined &&
				    parsed.Type == Messages.CommandParseResultType.Auth)
				{
					Console.WriteLine($"ERROR: Cannot use /auth command in current state ({_currentState}).");
					continue;
				}

				if ((_currentState == ClientState.Start || _currentState == ClientState.End) &&
				    (parsed.Type != Messages.CommandParseResultType.Unknown)) 
				{
					Console.WriteLine($"ERROR: Cannot perform action while not connected/ready ({_currentState}).");
					continue;
				}

				if (_currentState == ClientState.Joined &&
				    parsed.Type == Messages.CommandParseResultType.Auth)
				{
					Console.WriteLine($"ERROR: Cannot use /auth command in current state ({_currentState}).");
					continue;
				}

				string messageToSend = String.Empty;
				bool expectReply = false;

				switch (parsed.Type)
				{
					case Messages.CommandParseResultType.Auth:
						messageToSend = Utils.FormatAuthMessage(parsed.Username, parsed.DisplayName, parsed.Secret, _logger);
						if (messageToSend != String.Empty)
						{
							Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
							_currentUsername = parsed.Username;
							_currentDisplayName = parsed.DisplayName; // Set immediately
							expectReply = true;
							Console.WriteLine($"Display name set to: {_currentDisplayName}");
						}
						else
						{
							Console.WriteLine("ERROR: Could not format AUTH message.");
						}

						break;

					case Messages.CommandParseResultType.Join:
						messageToSend = Utils.FormatJoinMessage(parsed.ChannelId, _currentDisplayName, _logger);
						if (messageToSend != String.Empty)
						{
							Utils.SetState(ref _currentState, ClientState.Joining, _logger);

							expectReply = true;
						}
						else
						{
							Console.WriteLine("ERROR: Could not format JOIN message.");
						}

						break;

					case Messages.CommandParseResultType.Rename:
						string newName = Utils.Truncate(parsed.DisplayName, Utils.MaxDisplayNameLength, "Rename DisplayName", _logger);
						if (Utils.IsValidDisplayName(newName))
						{
							_currentDisplayName = newName;
							Console.WriteLine($"Display name changed to: {_currentDisplayName}");
							_logger.LogInformation("Local display name changed to {DisplayName}", _currentDisplayName);
						}
						else
						{
							Console.WriteLine("ERROR: Invalid display name format/characters for /rename.");
						}

						break;

					case Messages.CommandParseResultType.Help:
						Utils.PrintHelp(); // Defined later, same as before
						break;

					case Messages.CommandParseResultType.ChatMessage:
						messageToSend = Utils.FormatMsgMessage(_currentDisplayName, parsed.OriginalInput, _logger);
						if (messageToSend == String.Empty)
						{
							Console.WriteLine("ERROR: Could not format chat message.");
						}

						break;

					case Messages.CommandParseResultType.Unknown:
						// Error already printed by parser
						break;
				}

				if (messageToSend != String.Empty)
				{
					if (expectReply)
					{
						_waitForReply.Reset();
						StartReplyTimeout();
					}

					await SendMessageToServerAsync(messageToSend); // Uses raw socket send

					// Confirmation messages after sending
					if (parsed.Type == Messages.CommandParseResultType.Join && messageToSend != String.Empty)
					{
						Console.WriteLine($"Request sent to join channel '{parsed.ChannelId}'. Waiting for reply...");
						// Set _currentChannelId ONLY after REPLY OK in HandleReply
						_currentChannelId = parsed.ChannelId; // Update the field *now* so HandleReply can use it on success
					}
					else if (parsed.Type == Messages.CommandParseResultType.Auth && messageToSend != String.Empty)
					{
						Console.WriteLine($"Authentication request sent for user '{_currentUsername}'. Waiting for reply...");
					}
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			_logger.LogInformation("User input loop cancelled.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error in user input loop.");
			await InitiateShutdownAsync($"User input loop ERROR: {ex.Message}");
			Environment.ExitCode = 1;
		}

		_logger.LogDebug("User input loop finished.");
	}

	private async Task SendMessageToServerAsync(string message)
	{
		// Ensure the message already ends with CRLF (handled by Utils.Format*)
		if (_socket == null || !_socket.Connected || _currentState == ClientState.End)
		{
			_logger.LogWarning("Cannot send message, socket is null, not connected, or client is closing/closed.");
			return;
		}

		try
		{
			byte[] messageBytes = Encoding.ASCII.GetBytes(message);
			ReadOnlyMemory<byte> memoryBytes = messageBytes;

			int bytesSent = await _socket.SendAsync(memoryBytes, SocketFlags.None, _cts.Token);
			// Basic check: TCP should ideally send all or error out, but good to log.
			if (bytesSent < messageBytes.Length)
			{
				_logger.LogWarning("Potentially incomplete send: Sent {BytesSent}/{TotalBytes} bytes for message: {Message}", bytesSent, messageBytes.Length, message.TrimEnd());
				// More robust handling might involve retrying the remaining bytes, but often indicates a deeper issue.
			}

			_logger.LogDebug("Sent message ({BytesSent} bytes): {Message}", bytesSent, message.TrimEnd()); // Log without CRLF
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Send operation cancelled.");
		}
		catch (SocketException ex)
		{
			_logger.LogError(ex, "Socket error sending data. Connection may be lost.");
			await InitiateShutdownAsync("Connection lost during send.");
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Send attempted on a disposed socket.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error sending data.");
			await InitiateShutdownAsync($"Error sending data: {ex.Message}");
		}
	}

	private void StartReplyTimeout()
	{
		_replyTimeoutCts?.Cancel();
		_replyTimeoutCts?.Dispose();
		_replyTimeoutCts = new CancellationTokenSource();
		CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _replyTimeoutCts.Token);

		Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token).ContinueWith(async t =>
		{
			if (!t.IsCanceled) // Timeout occurred
			{
				_logger.LogError("Timeout: No REPLY received within 5 seconds for AUTH/JOIN request.");
				Console.WriteLine("ERROR: Server did not reply in time.");
				_waitForReply.Set();
				await SendErrAndShutdownAsync("Timeout waiting for server REPLY.");
			}

			linkedCts.Dispose();
		}, TaskScheduler.Default);
	}

	private async Task SendErrAndShutdownAsync(string errorMessage)
	{
		if (_currentState != ClientState.End)
		{
			string errMessage = Utils.FormatErrorMessage(_currentDisplayName, errorMessage, _logger);
			await SendMessageToServerAsync(errMessage);
		}

		await InitiateShutdownAsync($"Error condition: {errorMessage}", sendByeToServer: false);
		Environment.ExitCode = 1;
	}

	private async Task InitiateShutdownAsync(string reason, bool sendByeToServer = true)
	{
		if (_currentState == ClientState.End) return;

		_logger.LogInformation("Initiating shutdown. Reason: {Reason}", reason);
		_logger.LogDebug($"Before sending BYE. sendByeToServer: {sendByeToServer}, _socket.Connected: {_socket.Connected}");
		// Attempt to send BYE using the raw socket SendMessageToServerAsync
		if (sendByeToServer && _socket.Connected)
		{
			_logger.LogInformation("Attempting to send BYE message to server.");
			string byeMessage = Utils.FormatByeMessage(_currentDisplayName, _logger);
			try
			{
				// Send BYE with a short timeout specific to this operation
				using var byeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				byte[] byeBytes = Encoding.ASCII.GetBytes(byeMessage);
				await _socket.SendAsync(byeBytes, SocketFlags.None, byeCts.Token);
				_logger.LogInformation("BYE message sent.");
			}
			catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
			{
				_logger.LogWarning(ex, "Failed to send BYE message during shutdown (socket likely closed).");
			}
		}

		_cts?.Cancel();
		_replyTimeoutCts?.Cancel();
		_waitForReply?.Set();
	}

	public void OwnDispose()
	{
		_logger.LogDebug("OwnDispose executing...");

		// Ensure state is set to End before releasing resources
		Utils.SetState(ref _currentState, ClientState.End, _logger);

		// Dispose Tokens and WaitHandles - they should have been cancelled already
		// Use try-catch around each Dispose as they can throw ObjectDisposedException if already handled somehow
		try
		{
			_replyTimeoutCts?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during ReplyTimeoutCts Dispose.");
		}

		_replyTimeoutCts = null;

		try
		{
			_cts?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during main Cts Dispose.");
		}

		_cts = null; // *** Mark as disposed ***

		try
		{
			_waitForReply?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during WaitForReply Dispose.");
		}

		_waitForReply = null;

		// Dispose the socket
		var socketToDispose = _socket;
		_socket = null; // Nullify member variable

		if (socketToDispose != null)
		{
			_logger.LogDebug("Disposing socket...");
			try
			{
				// Optional: Check Connected before Shutdown, though Close/Dispose should handle it
				if (socketToDispose.Connected)
				{
					socketToDispose.Shutdown(SocketShutdown.Both);
					_logger.LogDebug("Socket Shutdown called.");
				}

				socketToDispose.Close(); // Close the connection
				_logger.LogDebug("Socket Close called.");
			}
			catch (Exception ex) // Catch errors during Shutdown/Close
			{
				_logger.LogWarning(ex, "Exception during socket Shutdown/Close.");
			}
			finally // Ensure Dispose is always called on the socket object
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
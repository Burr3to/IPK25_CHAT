namespace IPK25_CHAT.Tcp;

public class TcpChatClient
{
	// --- Dependencies ---
	private readonly ILogger<TcpChatClient> _logger;
	private readonly UserInputParser _userInputParser; // Injected parser for console input

	// --- Connection Fields ---
	private string _serverHost;
	private int _port;
	private Socket _socket;

	// --- Receive Buffer Fields ---
	private string _receiveMessage = string.Empty; // Buffer for accumulating partial messages
	private readonly byte[] _receiveBuffer = new byte[4096]; // Buffer for socket ReceiveAsync

	// --- Client State & Identity ---
	private ClientState _currentState;
	private string _currentDisplayName; // Client's display name after successful AUTH
	private string _currentUsername; // Client's username after successful AUTH
	private string _currentChannelId; // Channel ID after successful JOIN or default

	// --- Control & Synchronization ---
	private CancellationTokenSource _cts; // Main cancellation token source for client operations
	private CancellationTokenSource _replyTimeoutCts; // Specific CTS for REPLY timeouts
	private ManualResetEventSlim _waitForReply; // Used to block user input while waiting for a server REPLY


	// Constructor: Initializes the client with dependencies and sets up shutdown handling.
	public TcpChatClient(ILogger<TcpChatClient> logger, UserInputParser userInputParser)
	{
		_logger = logger;
		_userInputParser = userInputParser;

		_currentState = ClientState.Start;
		_cts = new CancellationTokenSource();
		_waitForReply = new ManualResetEventSlim(true); // Start as signaled (not waiting)

		// Hook into Ctrl+C to initiate graceful shutdown
		Console.CancelKeyPress += async (sender, e) =>
		{
			e.Cancel = true; // Prevent default process termination
			_logger.LogInformation("Ctrl+C detected. Initiating graceful shutdown...");
			try
			{
				// Signal shutdown; indicate it's client initiated (EOF/Ctrl+C/D)
				await InitiateShutdownAsync("User initiated shutdown (Ctrl+C).", isClientInitiatedEof: true);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Exception occurred during InitiateShutdownAsync called from CancelKeyPress.");
			}
		};
	}


	// --- Public Entry Point ---

	// Starts the client: resolves server address, connects, and begins receive/send loops.
	public async Task StartClientAsync(ArgumentParser.Options options)
	{
		_serverHost = options.Server;
		_port = options.Port;

		Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Initial state
		IPAddress serverIpAddress = Utils.GetFirstIPv4Address(_serverHost);

		if (serverIpAddress == null)
		{
			_logger.LogError("Failed to resolve server address: {ServerHost}", _serverHost);
			Console.WriteLine($"ERROR: Could not resolve server address '{_serverHost}'.");
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			Environment.ExitCode = 1;
			return; // Exit if resolution fails
		}

		try
		{
			_logger.LogInformation("Connecting to {ServerAddress}:{Port} via TCP...", serverIpAddress, _port);
			_socket = new Socket(serverIpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

			// Connect asynchronously with cancellation support
			await _socket.ConnectAsync(serverIpAddress, _port, _cts.Token);

			// Connection successful
			_logger.LogInformation("Connected to server.");
			_logger.LogInformation("Connected to server. Please authenticate using /auth <Username> <Secret> <DisplayName>");
			Utils.SetState(ref _currentState, ClientState.Connected, _logger);

			// Start the concurrent receive and user input handling tasks
			var receiveTask = ReceiveMessagesAsync(_cts.Token);
			var sendTask = HandleUserInputAsync(_cts.Token);

			// Wait for either task to complete (e.g., due to error, cancellation, or disconnect)
			await Task.WhenAny(receiveTask, sendTask);

			_logger.LogInformation("Main client loops finished.");
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
			_logger.LogInformation("Client connection or operation cancelled.");
			// Shutdown was called elsewhere
		}
		catch (SocketException ex)
		{
			_logger.LogError(ex, "Socket error during client connection or operation: {SocketErrorCode}", ex.SocketErrorCode);
			Console.WriteLine($"ERROR: Network error: {ex.Message}");
			// Initiate shutdown on socket error
			await InitiateShutdownAsync($"Socket error during startup: {ex.Message}");
			Environment.ExitCode = 1;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error during client startup or operation.");
			Console.WriteLine($"ERROR: Unexpected startup error: {ex.Message}");
			// Initiate shutdown on unexpected error
			await InitiateShutdownAsync($"Unexpected startup ERROR: {ex.Message}");
			Environment.ExitCode = 1;
		}
		finally
		{
			// Ensure resources are cleaned up regardless of how the task exits
			OwnDispose();
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			_logger.LogInformation("Client StartClientAsync finished, resources disposed.");
		}
	}

	// --- Asynchronous Operation Loops ---

	// Handles continuous receiving of data from the server socket.
	private async Task ReceiveMessagesAsync(CancellationToken token)
	{
		_logger.LogDebug("Receive loop started.");
		try
		{
			// Use Memory<byte> for efficient buffer handling
			Memory<byte> buffer = _receiveBuffer.AsMemory();

			// Loop while cancellation is not requested, socket is connected, and client is not ending
			while (!token.IsCancellationRequested && _socket.Connected && _currentState != ClientState.End)
			{
				int bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);

				if (bytesRead == 0) // Indicates graceful disconnect by the server
				{
					_logger.LogWarning("Server disconnected gracefully (received 0 bytes).");
					await InitiateShutdownAsync("Server closed the connection.", isClientInitiatedEof: false); // Server initiated shutdown
					break;
				}

				// Decode received bytes and append to the accumulator buffer
				string receivedText = Encoding.ASCII.GetString(_receiveBuffer, 0, bytesRead);
				_receiveMessage += receivedText;

				_logger.LogDebug("Received {BytesRead} bytes. Accumulator size: {Size}", bytesRead, _receiveMessage.Length);

				// Process any complete messages found in the buffer
				await ProcessReceivedBufferAsync();

				// Check state again after processing buffer, as a message might have triggered shutdown
				if (_currentState == ClientState.End || token.IsCancellationRequested)
					break;
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			_logger.LogInformation("Receive loop cancelled.");
		}
		catch (SocketException ex)
		{
			_logger.LogWarning(ex, "Socket error during receive (connection likely lost): {SocketErrorCode}", ex.SocketErrorCode);
			await InitiateShutdownAsync("Connection lost during receive."); // Error initiated shutdown
		}
		catch (ObjectDisposedException)
		{
			// Expected if socket is disposed while ReceiveAsync is pending
			_logger.LogDebug("Receive loop attempted to use a disposed socket.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error in receive loop.");
			await InitiateShutdownAsync($"Receive loop ERROR: {ex.Message}"); // Unexpected error initiated shutdown
			Environment.ExitCode = 1;
		}

		_logger.LogDebug("Receive loop finished.");
	}

	// Handles the loop for reading user input, parsing it, and validating state.
	private async Task HandleUserInputAsync(CancellationToken token)
	{
		_logger.LogDebug("User input loop started.");
		try
		{
			// Loop while cancellation is not requested and client is not ending
			while (!token.IsCancellationRequested && _currentState != ClientState.End)
			{
				// If we sent a message requiring a REPLY, block input until _waitForReply is signaled
				if (!_waitForReply.IsSet)
				{
					_logger.LogDebug("Waiting for server reply...");
					try
					{
						// Use Task.Run to allow Console.ReadLine to run on a background thread
						// while keeping the main loop async. Wait for the wait handle with the token.
						await Task.Run(() => _waitForReply.Wait(token), token);
					}
					catch (OperationCanceledException)
					{
						// If the waiting token is cancelled (e.g., by Ctrl+C or shutdown)
						break; // Exit the loop
					}

					// Check main cancellation and state again after the wait completes
					if (token.IsCancellationRequested || _currentState == ClientState.End) break;
					_logger.LogDebug("Wait for server reply finished.");
				}


				// Read line from console, wrapped in Task.Run to prevent blocking the async method
				string input = await Task.Run(() => Console.ReadLine(), token);

				if (input == null) // Indicates End of Stream (Ctrl+D on Unix-like systems)
				{
					_logger.LogInformation("End of input detected (Ctrl+D). Initiating graceful shutdown...");
					await InitiateShutdownAsync("User initiated shutdown (Ctrl+D).", isClientInitiatedEof: true);
					break; // Exit loop
				}

				if (string.IsNullOrWhiteSpace(input)) continue; // Ignore empty or whitespace input

				// Use the injected UserInputParser to parse the raw input string
				var parsed = _userInputParser.ParseUserInput(input);

				// State validation
				if (_currentState != ClientState.Joined &&
				    (parsed.Type == UserInputParser.CommandParseResultType.ChatMessage || parsed.Type == UserInputParser.CommandParseResultType.Join))
				{
					Console.WriteLine("ERROR: Cannot send messages or join channels until authenticated and joined.");
					continue;
				}

				if (_currentState != ClientState.Connected && _currentState != ClientState.Joined &&
				    parsed.Type == UserInputParser.CommandParseResultType.Auth)
				{
					Console.WriteLine($"ERROR: Cannot use /auth command in current state ({_currentState}).");
					continue;
				}

				if ((_currentState == ClientState.Start || _currentState == ClientState.End) &&
				    (parsed.Type != UserInputParser.CommandParseResultType.Unknown))
				{
					Console.WriteLine($"ERROR: Cannot perform action while not connected/ready ({_currentState}).");
					continue;
				}

				if (_currentState == ClientState.Joined &&
				    parsed.Type == UserInputParser.CommandParseResultType.Auth)
				{
					Console.WriteLine($"ERROR: Cannot use /auth command in current state ({_currentState}).");
					continue;
				}


				// Process the valid (or help/unknown) command in a separate method
				await ProcessParsedUserInputAsync(parsed); // Pass the parsed input and token
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			_logger.LogInformation("User input loop cancelled.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error in TCP user input loop.");
			await InitiateShutdownAsync($"TCP user input loop ERROR: {ex.Message}");
			Environment.ExitCode = 1; // Indicate error exit code
		}
		finally
		{
			_logger.LogDebug("TCP user input loop finished.");
			// Ensure main CTS is cancelled if loop exits unexpectedly (e.g., error not caught above)
			if (!_cts.IsCancellationRequested && _currentState != ClientState.End) // Only cancel if not already shutting down
			{
				_logger.LogWarning("TCP user input loop exiting unexpectedly. Triggering main cancellation.");
				_cts.Cancel(); // Trigger cancellation for the receive loop and StartClientAsync wait
			}
		}
	}


	// --- Message Processing Helpers ---

	// Helper to process complete messages found in the receive buffer (_receiveMessage).
	private async Task ProcessReceivedBufferAsync()
	{
		int messageEndIndex;

		// Loop as long as a complete message (ending with CRLF) is found in the buffer
		while ((messageEndIndex = _receiveMessage.IndexOf(ProtocolValidation.CRLF, StringComparison.Ordinal)) >= 0)
		{
			// Extract the complete message string
			string completeMessage = _receiveMessage.Substring(0, messageEndIndex);
			_logger.LogDebug("Found complete message in buffer: {Message}", completeMessage);

			await ProcessServerMessageAsync(completeMessage);

			// Remove the processed message and its CRLF from the buffer
			int removeLength = messageEndIndex + ProtocolValidation.CRLF.Length;
			_receiveMessage = _receiveMessage.Substring(removeLength);

			// Check if processing the message triggered shutdown (e.g., received BYE or ERR)
			if (_currentState == ClientState.End || _cts.IsCancellationRequested)
				return; // Stop processing buffer if shutting down
		}

		_logger.LogDebug("Finished processing accumulator for now. Remaining size: {Size}", _receiveMessage.Length);
	}

	// Processes a single, complete raw message string received from the server.
	// Parses it and handles based on type and current client state.
	private async Task ProcessServerMessageAsync(string rawMessage)
	{
		// Use the static ServerMessageParser to parse the raw string
		var parsedMessage = ServerMessageParser.ParseServerMessage(rawMessage, _logger);

		// Handle protocol-level errors/BYE that can happen in many states first
		// These typically lead to shutdown regardless of specific state logic
		if (parsedMessage.Type == ServerMessageType.Err)
		{
			_logger.LogDebug("Received ERR from {DisplayName}: {Content}", parsedMessage.DisplayName, parsedMessage.Content);
			Console.WriteLine($"ERROR FROM {parsedMessage.DisplayName}: {parsedMessage.Content}");
			await InitiateShutdownAsync($"Received ERR message from {parsedMessage.DisplayName}.", isClientInitiatedEof: false);
			return;
		}

		if (parsedMessage.Type == ServerMessageType.Bye)
		{
			_logger.LogInformation("Received BYE from {DisplayName}. Closing connection.", parsedMessage.DisplayName);
			await InitiateShutdownAsync($"Received BYE message from {parsedMessage.DisplayName}.", isClientInitiatedEof: false);
			return;
		}

		_logger.LogInformation("Server -> Client: {Message}", parsedMessage.OriginalMessage);

		// Handle messages based on the current client state
		switch (_currentState)
		{
			case ClientState.Authenticating:
			case ClientState.Joining:
				// While waiting for a REPLY, only process REPLY messages
				if (parsedMessage.Type == ServerMessageType.Reply)
				{
					HandleReply(parsedMessage); // Process the REPLY message
				}
				else
				{
					// Received something other than REPLY while waiting for one
					_logger.LogWarning("Received unexpected message ({Type}) while waiting for REPLY in state {State}. Original: {Original}",
						parsedMessage.Type, _currentState, parsedMessage.OriginalMessage);
					Console.WriteLine($"ERROR: Received unexpected server message while waiting for reply ({parsedMessage.Type}).");
					// Optional: Depending on protocol strictness, this might be a fatal error.
					await SendErrAndShutdownAsync($"Unexpected message ({parsedMessage.Type}) received while waiting for REPLY.");
				}

				break;

			case ClientState.Joined:
				// In the Joined state, expect MSG, potentially ERR/BYE (handled above), or unexpected REPLY
				switch (parsedMessage.Type)
				{
					case ServerMessageType.Msg:
						// Display chat message to the user console
						Console.WriteLine($"{parsedMessage.DisplayName}: {parsedMessage.Content}");
						break;
					case ServerMessageType.Reply:
						// Received a REPLY when not explicitly waiting for one (e.g., after AUTH/JOIN).
						_logger.LogWarning("Received unexpected REPLY in Joined state. Original: {OriginalMessage}", parsedMessage.OriginalMessage);
						if (parsedMessage.IsOkReply)
							Console.WriteLine($"Action Success: {parsedMessage.Content}");
						else
							Console.WriteLine($"Action Failure: {parsedMessage.Content}");


						break;
					// ERR and BYE are handled at the beginning of the method.
					default:
						// Received a message type not expected in the Joined state (and not ERR/BYE)
						_logger.LogWarning("Received message undefined/malformed ({Type}) in Joined state. Original: {Original}",
							parsedMessage.Type, parsedMessage.OriginalMessage);
						Console.WriteLine($"ERROR: Received unhandled server message in Joined state ({parsedMessage.Type}).");
						await SendErrAndShutdownAsync($"Received unhandled message ({parsedMessage.Type}) in Joined state.");
						break;
				}

				break;

			case ClientState.Connected:
				// Received a message before authentication. Only AUTH command should be sent.
				// Only REPLY to AUTH should be received. Other messages are unexpected.
				_logger.LogWarning("Received unexpected message ({Type}) in state {State} before authentication. Original: {Original}",
					parsedMessage.Type, _currentState, parsedMessage.OriginalMessage);
				Console.WriteLine($"ERROR: Received unexpected server message before authentication ({parsedMessage.Type}).");
				// Optional: Consider shutting down or sending ERR here.
				await SendErrAndShutdownAsync($"Unexpected message ({parsedMessage.Type}) received before authentication.");
				break;

			case ClientState.Start: // Socket not yet connected/setup
			case ClientState.End: // Client is shutting down/closed
				// Messages received in these states are generally ignored, though StartClientAsync/ReceiveMessagesAsync
				// might trigger shutdown if connection fails immediately.
				_logger.LogDebug("Ignoring received message ({Type}) in state {State}.", parsedMessage.Type, _currentState);
				break;

			default:
				// Should not happen if state machine is correctly implemented
				_logger.LogError("Received message ({Type}) in unexpected/unhandled state: {State}. Original: {Original}",
					parsedMessage.Type, _currentState, parsedMessage.OriginalMessage);
				Console.WriteLine($"ERROR: Received message in invalid state ({_currentState}).");
				// This indicates a logic error, likely fatal.
				await InitiateShutdownAsync($"Internal error: Message received in unhandled state {_currentState}.", isClientInitiatedEof: false);
				break;
		}
	}

	// Handles a parsed server REPLY message. Updates state based on current context and REPLY status.
	private void HandleReply(ParsedServerMessage reply)
	{
		// Ensure reply timeout is cancelled when a reply is received
		_replyTimeoutCts?.Cancel();
		_replyTimeoutCts?.Dispose(); // Dispose immediately after cancelling
		_replyTimeoutCts = null;

		_logger.LogDebug($"Server Reply: {(reply.IsOkReply ? "OK" : "NOK")} - {reply.Content}");

		// Determine what the client was waiting for a reply to based on the current state
		bool wasAuth = _currentState == ClientState.Authenticating;
		bool wasJoin = _currentState == ClientState.Joining;

		if (reply.IsOkReply)
		{
			// Handle successful replies
			if (wasAuth)
			{
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				_currentChannelId = "default";
				Console.WriteLine($"Action Success: {reply.Content}"); // Display server's content
			}
			else if (wasJoin)
			{
				_logger.LogInformation($"Join channel {_currentChannelId} successful.");
				// Set state to Joined upon OK reply for JOIN
				Utils.SetState(ref _currentState, ClientState.Joined, _logger);
				// _currentChannelId is already set to the requested ID before sending JOIN
				Console.WriteLine($"Action Success: {reply.Content}"); // Display server's content
			}
			else
			{
				// Received an OK REPLY but client was not in a state waiting for one
				_logger.LogWarning("Received OK REPLY but client was not in Authenticating or Joining state ({State}). Content: {Content}",
					_currentState, reply.Content);
				if (reply.IsOkReply)
					Console.WriteLine($"Action Success: {reply.Content}");
				else
					Console.WriteLine($"Action Failure: {reply.Content}");
			}
		}
		else // NOK Reply
		{
			// Handle failed replies
			_logger.LogDebug($"Handling NOK reply. Was AUTH: {wasAuth}, Was JOIN: {wasJoin}, Content: '{reply.Content}'");
			Console.WriteLine($"Action Failure: {reply.Content}"); // Always display server's error message

			if (wasAuth)
			{
				_logger.LogWarning("Authentication failed: {Reason}", reply.Content);
				Utils.SetState(ref _currentState, ClientState.Connected, _logger);
				_currentUsername = null; // Clear local credentials on failure
				_currentDisplayName = null;
				Console.WriteLine("ERROR: Authentication failed. Please check credentials and try /auth again.");
			}
			else if (wasJoin)
			{
				_logger.LogWarning("Join failed: {Reason}", reply.Content);
				Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
				_currentChannelId = null; // Clear pending channel ID on failure
				Console.WriteLine($"ERROR: Failed to join channel {_currentState}.");
			}
			else
			{
				// Received a NOK REPLY when not explicitly waiting for one
				_logger.LogWarning("Received NOK REPLY but client was not in Authenticating or Joining state ({State}). Content: {Content}",
					_currentState, reply.Content);
				if (reply.IsOkReply)
					Console.WriteLine($"Action Success: {reply.Content}");
				else
					Console.WriteLine($"Action Failure: {reply.Content}");
			}
		}

		// Signal the input loop to continue after processing the reply
		_waitForReply.Set();
	}
	
	// --- User Input Processing ---
	// Processes a parsed user input command: formats message, sends, and manages reply expectation.
	private async Task ProcessParsedUserInputAsync(UserInputParser.ParsedUserInput parsedInput)
	{
		string messageToSend = String.Empty; // String to send to server (if any)
		bool expectReply = false; // Flag to indicate if the command expects a server REPLY

		switch (parsedInput.Type)
		{
			case UserInputParser.CommandParseResultType.Auth:
				// Format AUTH message using static ClientMessageFormatter, passing logger
				messageToSend = ClientMessageFormatter.FormatAuthMessage(parsedInput.Username, parsedInput.DisplayName, parsedInput.Secret, _logger);
				if (messageToSend != null) // Check if formatting was successful (parameters valid)
				{
					// Set state and store local info *before* sending the request
					Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
					_currentUsername = parsedInput.Username;
					_currentDisplayName = parsedInput.DisplayName; // Store display name
					expectReply = true; // AUTH expects a REPLY
					_logger.LogWarning($"Display name set locally to: {_currentDisplayName}"); // User feedback
				}
				else
				{
					// Formatting failed (invalid params), error logged by formatter
					Console.WriteLine("ERROR: Could not format AUTH message due to invalid parameters.");
				}

				break;

			case UserInputParser.CommandParseResultType.Join:
				// Check prerequisites (must be authenticated) - Redundant if IsCommandAllowedInCurrentState is perfect, but safe
				if (_currentState != ClientState.Authenticating && _currentState != ClientState.Joined) // JOIN is allowed in Authenticated or already Joined (to switch channels)
				{
					_logger.LogWarning("Attempted JOIN from incorrect state after validation: {State}", _currentState);
					Console.WriteLine("ERROR: Must be authenticated or in a channel to join.");
					return; // Exit method
				}

				if (string.IsNullOrEmpty(_currentDisplayName)) // Also need a display name
				{
					_logger.LogWarning("Attempted JOIN without a display name ({State})", _currentState);
					Console.WriteLine("ERROR: Must authenticate successfully first to set your display name.");
					return; // Exit method
				}


				// Format JOIN message using static ClientMessageFormatter, passing logger
				messageToSend = ClientMessageFormatter.FormatJoinMessage(parsedInput.ChannelId, _currentDisplayName, _logger);
				if (messageToSend != null)
				{
					// Set state and store channel ID *before* sending the request
					Utils.SetState(ref _currentState, ClientState.Joining, _logger);
					_currentChannelId = parsedInput.ChannelId; // Store pending channel ID
					expectReply = true; // JOIN expects a REPLY
				}
				else
				{
					// Formatting failed, error logged by formatter
					Console.WriteLine("ERROR: Could not format JOIN message due to invalid parameters.");
				}

				break;

			case UserInputParser.CommandParseResultType.Rename:
				// Truncate display name using static ClientMessageFormatter helper, passing logger
				string newName = ClientMessageFormatter.Truncate(parsedInput.DisplayName, ProtocolValidation.MaxDisplayNameLength, "Rename DisplayName", _logger);
				// Validate truncated name using static ProtocolValidation
				if (ProtocolValidation.IsValidDisplayName(newName))
				{
					_currentDisplayName = newName; // Update local display name
					_logger.LogInformation("Local display name changed to {DisplayName}", _currentDisplayName);
				}
				else
				{
					// Validation failed, error logged by Truncate/IsValidDisplayName
					Console.WriteLine("ERROR: Invalid display name format/characters for /rename.");
				}

				messageToSend = String.Empty; // No server message for local command
				expectReply = false; // No reply expected for local command
				break;

			case UserInputParser.CommandParseResultType.Help:
				Utils.PrintHelp(); // Static call
				messageToSend = String.Empty; // No server message
				expectReply = false;
				break;

			case UserInputParser.CommandParseResultType.ChatMessage:
				// Check prerequisites (must be joined) 
				if (_currentState != ClientState.Joined) // Chat requires Joined state
				{
					_logger.LogWarning("Attempted ChatMessage from incorrect state after validation: {State}", _currentState);
					Console.WriteLine("ERROR: You must join a channel to send chat messages.");
					return; // Exit method
				}

				if (string.IsNullOrEmpty(_currentDisplayName) || string.IsNullOrEmpty(_currentChannelId)) // Also need display name and channel
				{
					_logger.LogWarning("Attempted ChatMessage without display name or channel ({State})", _currentState);
					Console.WriteLine("ERROR: Authentication and joining a channel are required to chat.");
					return; // Exit method
				}

				// Format MSG message using static ClientMessageFormatter, passing logger
				messageToSend = ClientMessageFormatter.FormatMsgMessage(_currentDisplayName, parsedInput.OriginalInput, _logger);
				if (messageToSend == null) // Check for null from formatter (invalid content)
				{
					// Formatting failed, error logged by formatter
					Console.WriteLine("ERROR: Could not format chat message due to invalid parameters.");
				}

				expectReply = false; // Chat messages typically don't require a REPLY in this protocol
				break;

			case UserInputParser.CommandParseResultType.Unknown:
				// Error message already printed by the UserInputParser.
				// No server message to send.
				messageToSend = String.Empty;
				expectReply = false;
				break;

			// Default case for safety, though all defined types are covered above
			default:
				_logger.LogError("Unhandled ParsedUserInput type in ProcessParsedUserInputAsync: {Type}", parsedInput.Type);
				Console.WriteLine($"ERROR: Unhandled command type ({parsedInput.Type}).");
				messageToSend = String.Empty;
				expectReply = false;
				break;
		}

		// If a message string was successfully formatted and the command wasn't purely local
		if (!string.IsNullOrEmpty(messageToSend))
		{
			// If this command requires a server REPLY, block input and start timeout BEFORE sending
			if (expectReply)
			{
				_waitForReply.Reset(); // Block the HandleUserInputAsync loop from reading new input
				StartReplyTimeout(); // Start the timeout timer for the expected reply
			}

			// Send the formatted message string to the server using the socket
			await SendMessageToServerAsync(messageToSend);
		}
	}

	// --- Sending Data ---

	// Sends a raw message string to the server over the TCP socket.
	private async Task SendMessageToServerAsync(string message)
	{
		// Ensure the message already ends with CRLF (handled by ClientMessageFormatter.Format*)
		if (_socket == null || !_socket.Connected || _currentState == ClientState.End)
		{
			_logger.LogWarning("Cannot send message, socket is null, not connected, or client is ending/ended.");
			return;
		}

		try
		{
			// Encode the string to bytes (assuming ASCII or UTF8 compatible with protocol)
			byte[] messageBytes = Encoding.ASCII.GetBytes(message);
			ReadOnlyMemory<byte> memoryBytes = messageBytes.AsMemory();

			// Send the bytes asynchronously with cancellation support
			int bytesSent = await _socket.SendAsync(memoryBytes, SocketFlags.None, _cts.Token);

			// Log the sent message (without CRLF for cleaner logs)
			_logger.LogDebug("Sent message ({BytesSent} bytes): {Message}", bytesSent, message.TrimEnd(ProtocolValidation.CRLF.ToCharArray()));

			// Check if the full message was sent - typically TCP handles this, but good practice
			if (bytesSent < messageBytes.Length)
			{
				_logger.LogWarning("Incomplete send: Sent {BytesSent}/{TotalBytes} bytes for message. Connection issue?", bytesSent, messageBytes.Length);
				// Depending on protocol, incomplete send might require shutdown
				await InitiateShutdownAsync($"Incomplete data sent: {bytesSent}/{messageBytes.Length} bytes.");
			}
		}
		catch (OperationCanceledException)
		{
			_logger.LogDebug("Send operation cancelled.");
		}
		catch (SocketException ex)
		{
			_logger.LogError(ex, "Socket error sending data. Connection may be lost: {SocketErrorCode}", ex.SocketErrorCode);
			Console.WriteLine($"ERROR: Network error during send: {ex.Message}");
			// Initiate shutdown on socket error
			await InitiateShutdownAsync("Connection lost during send.");
		}
		catch (ObjectDisposedException)
		{
			// Expected if socket is disposed while SendAsync is pending
			_logger.LogDebug("Send attempted on a disposed socket.");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"ERROR: Unexpected error during send: {ex.Message}");
			await InitiateShutdownAsync($"Error sending data: {ex.Message}");
		}
	}

	// Starts the timeout for waiting for a server REPLY after sending a command that expects one.
	private void StartReplyTimeout()
	{
		// Cancel and dispose of any existing timeout source
		_replyTimeoutCts?.Cancel();
		_replyTimeoutCts?.Dispose();
		_replyTimeoutCts = null; // Set to null after disposing

		// Create a new CTS for the reply timeout duration
		_replyTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5 seconds timeout example

		// Create a linked token source so timeout is also cancelled if the main CTS is cancelled
		CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _replyTimeoutCts.Token);

		_logger.LogDebug("Started TCP reply timeout ({Timeout}s).", 5);

		// Schedule a task to run after the timeout or cancellation
		Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token).ContinueWith(async t =>
		{
			// Check if the task completed due to timeout (not cancellation)
			if (!t.IsCanceled)
			{
				_logger.LogError("Timeout: No REPLY received within 5 seconds while in state {State}.", _currentState);
				Console.WriteLine("ERROR: Server did not reply in time.");

				// Unblock the input loop
				_waitForReply.Set();

				await SendErrAndShutdownAsync("Timeout waiting for server REPLY.");
			}

			linkedCts.Dispose();
		}, TaskScheduler.Default); // Run continuation on default task scheduler
	}


	// Sends an ERR message to the server and initiates shutdown.
	// Called when the client detects a severe internal error or protocol violation.
	private async Task SendErrAndShutdownAsync(string errorMessage)
	{
		// Only attempt to send ERR if the socket is connected and client is not already ending
		if (_socket != null && _socket.Connected && _currentState != ClientState.End)
		{
			_logger.LogError("Client sending ERR to server and shutting down: {ErrorMessage}", errorMessage);
			// Format the ERR message using static ClientMessageFormatter, passing logger
			string errMessage = ClientMessageFormatter.FormatErrorMessage(_currentDisplayName ?? "Client", errorMessage, _logger);
			if (errMessage != null)
			{
				try
				{
					using var errCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)); // Short timeout for send
					byte[] errBytes = Encoding.ASCII.GetBytes(errMessage);
					await _socket.SendAsync(errBytes, SocketFlags.None, errCts.Token);
					_logger.LogDebug("ERR message sent to server.");
				}
				catch (Exception ex) // Catch exceptions during this best-effort send
				{
					_logger.LogWarning(ex, "Failed to send ERR message to server during shutdown.");
				}
			}
		}
		else
		{
			_logger.LogError("Client initiating shutdown due to error, but cannot send ERR: {ErrorMessage}", errorMessage);
		}

		// Initiate the shutdown process (will handle canceling tasks and disposing resources)
		// Pass false for sendByeToServer as ERR/internal error initiated shutdown, not a user BYE command.
		await InitiateShutdownAsync($"Error condition: {errorMessage}", isClientInitiatedEof: false);
		Environment.ExitCode = 1; // Indicate error exit code
	}

	// Initiates a graceful shutdown sequence for the client.
	// isClientInitiatedEof is true if triggered by user input EOF (Ctrl+C/D), false otherwise (server BYE, error, etc.).
	private async Task InitiateShutdownAsync(string reason, bool isClientInitiatedEof = false)
	{
		// Check if shutdown is already in progress to avoid double execution
		if (_cts.IsCancellationRequested)
		{
			_logger.LogDebug("Shutdown already initiated. Reason: {Reason}", reason);
			return; // Already shutting down
		}

		_logger.LogInformation("Initiating shutdown. Reason: {Reason}", reason);

		// Signal cancellation to all tasks (ReceiveMessagesAsync, HandleUserInputAsync, any pending SendAsync/ConnectAsync)
		_cts.Cancel();

		// Attempt to send a BYE message to the server if appropriate:
		// - Socket is connected
		// - Client is in a state where sending BYE makes sense (Authenticated or Joined)
		// - Shutdown was initiated by the client's EOF (Ctrl+C/D), *not* by the server sending BYE or an internal error.
		if (isClientInitiatedEof && _socket != null && _socket.Connected && (_currentState == ClientState.Authenticating || _currentState == ClientState.Joined))
		{
			_logger.LogInformation("Attempting to send BYE message to server.");
			// Format the BYE message using static ClientMessageFormatter, passing logger
			string byeMessage = ClientMessageFormatter.FormatByeMessage(_currentDisplayName, _logger);
			if (byeMessage != null)
			{
				try
				{
					// Send BYE as a best-effort, non-reliable message during shutdown
					using var byeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1)); // Short timeout for send
					byte[] byeBytes = Encoding.ASCII.GetBytes(byeMessage);
					await _socket.SendAsync(byeBytes, SocketFlags.None, byeCts.Token);
					_logger.LogInformation("BYE message sent successfully.");
				}
				catch (Exception ex) // Catch exceptions during this best-effort send
				{
					_logger.LogWarning(ex, "Failed to send BYE message during shutdown (socket likely closed).");
				}
			}
			else
			{
				_logger.LogWarning("Failed to format BYE message during shutdown.");
			}
		}
		else
		{
			_logger.LogDebug("Skipping sending BYE message. isClientInitiatedEof={IsClientEOF}, SocketConnected={IsConnected}, State={State}, DisplayNameSet={DisplayNameSet}",
				isClientInitiatedEof, _socket?.Connected ?? false, _currentState, !string.IsNullOrEmpty(_currentDisplayName));
		}


		// Clean up resources (socket, CTS, etc.) immediately after signaling cancellation and attempting BYE send
		OwnDispose();
	}

	// Disposes of client resources (socket, cancellation token sources, wait handle).
	public void OwnDispose() // Not implementing IDisposable, but a manual cleanup method
	{
		_logger.LogDebug("OwnDispose executing...");

		// Set state to End early in dispose process
		Utils.SetState(ref _currentState, ClientState.End, _logger);

		// Dispose CancellationTokenSources - Dispose method is safe to call multiple times
		// It's good practice to dispose these, even if they were cancelled.
		try
		{
			_replyTimeoutCts?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during ReplyTimeoutCts Dispose.");
		}

		_replyTimeoutCts = null;

		// Dispose the main CTS last, as it cancels others potentially.
		// Ensure it's nullified to prevent trying to cancel/dispose again.
		var mainCtsToDispose = _cts;
		_cts = null; // Nullify member variable FIRST
		try
		{
			mainCtsToDispose?.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exception during main Cts Dispose.");
		}


		// Dispose the ManualResetEventSlim
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
		_socket = null; // Nullify member variable FIRST

		if (socketToDispose != null)
		{
			_logger.LogDebug("Disposing socket...");
			try
			{
				// Attempt graceful shutdown if still connected
				if (socketToDispose.Connected)
				{
					socketToDispose.Shutdown(SocketShutdown.Both);
					_logger.LogDebug("Socket Shutdown called.");
				}

				// Close the connection and release the handle
				socketToDispose.Close();
				_logger.LogDebug("Socket Close called.");
			}
			catch (Exception ex) // Catch errors during Shutdown/Close
			{
				_logger.LogWarning(ex, "Exception during socket Shutdown/Close.");
			}
			finally // Ensure Dispose is always called on the socket object, even if Shutdown/Close failed
			{
				try
				{
					socketToDispose.Dispose(); // Release socket resources
					_logger.LogDebug("Socket Dispose called.");
				}
				catch (Exception ex) // Catch errors during Dispose itself
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
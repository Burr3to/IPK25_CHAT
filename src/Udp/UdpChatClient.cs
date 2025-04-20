namespace IPK25_CHAT.Udp;

// Represents a UDP chat client. Handles connection setup, user input, sending messages
// with basic reliability, managing client state, and initiating shutdown.
public partial class UdpChatClient // Indicates this class definition is split across multiple files
{
	// --- Dependencies ---
	private readonly ILogger<UdpChatClient> _logger;
	private readonly UserInputParser _userInputParser; // Injected parser for console input (Renamed from _messageParser)

	// --- Connection Fields ---
	private string _serverHost;
	private int _port;
	private Socket _socket; // UDP socket
	private IPEndPoint _initialServerEndPoint; // Server endpoint provided on command line (target for AUTH)
	private IPEndPoint _currentServerEndPoint; // Actual server endpoint after receiving first packet (target for subsequent messages)

	// --- Client State & Identity ---
	private ClientState _currentState;
	private string _currentDisplayName; // Client's display name after successful AUTH
	private string _currentUsername; // Client's username after successful AUTH
	private string _currentChannelId; // Channel ID after successful JOIN

	// --- Pending State (during handshake) ---
	private string _pendingUsername; // Stored during AUTH request while waiting for REPLY
	private string _pendingSecret; // Stored during AUTH request while waiting for REPLY
	private string _pendingDisplayName; // Stored during AUTH request while waiting for REPLY

	// --- Reliability & Control ---
	private CancellationTokenSource _cts; // Main cancellation token source for client operations
	private volatile int _shutdownInitiatedFlag = 0;

	// Message ID counter for outgoing packets
	private ushort _nextMessageId = 0;

	// Dictionaries to track reliable messages awaiting response (CONFIRM or REPLY)
	private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pendingConfirms = new(); // For messages expecting a CONFIRM (like MSG)
	private readonly ConcurrentDictionary<ushort, TaskCompletionSource<ParsedServerMessage>> _pendingReplies = new(); // For messages expecting a REPLY (like AUTH, JOIN)

	// Constants for reliability (as per original code)
	private const int ReplyTimeoutMilliseconds = 5000;
	private const int ConfirmationTimeoutMilliseconds = 250; // Timeout waiting for a CONFIRM for
	private const int MaxRetries = 3; // Max retries for reliable messages (total attempts = 4)

	// Dictionary to prevent displaying duplicate incoming messages (MSG) - Used in Receive partial
	private readonly ConcurrentDictionary<ushort, byte> _processedIncomingMessageIds = new();


	// Constructor: Initializes the client with dependencies and sets up shutdown handling.
	public UdpChatClient(ILogger<UdpChatClient> logger, UserInputParser userInputParser) // Renamed parameter
	{
		_logger = logger;
		_userInputParser = userInputParser;

		_currentState = ClientState.Start;
		_cts = new CancellationTokenSource();

		// Hook into Ctrl+C to initiate graceful shutdown
		Console.CancelKeyPress += async (sender, e) =>
		{
			e.Cancel = true; // Prevent default process termination
			_logger.LogInformation("Ctrl+C detected. Initiating graceful shutdown...");
			try
			{
				// Signal shutdown; indicate it's client initiated (EOF/Ctrl+C/D)
				await InitiateShutdownAsync("User initiated shutdown (Ctrl+C).", true); // Pass true
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Exception occurred during InitiateShutdownAsync called from CancelKeyPress.");
			}
		};
	}

	// --- Public Entry Point ---

	// Starts the client: resolves server address, binds UDP socket, and begins receive/send loops.
	public async Task StartClientAsync(ArgumentParser.Options options)
	{
		_serverHost = options.Server;
		_port = options.Port;

		Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Initial state while resolving/binding
		IPAddress serverIpAddress = Utils.GetFirstIPv4Address(_serverHost);

		if (serverIpAddress == null)
		{
			_logger.LogError("Failed to resolve server address: {ServerHost}", _serverHost);
			Console.WriteLine($"ERROR: Could not resolve server address '{_serverHost}'.");
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			Environment.ExitCode = 1;
			return; // Exit if resolution fails
		}

		// Set initial target endpoint (AUTH packets go here)
		_initialServerEndPoint = new IPEndPoint(serverIpAddress, _port);
		_currentServerEndPoint = _initialServerEndPoint; // Target initial server first
		_logger.LogInformation("Targeting initial server endpoint: {InitialEndPoint}", _initialServerEndPoint);

		try
		{
			_logger.LogInformation("Creating UDP socket and binding to a local port...");
			_socket = new Socket(serverIpAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
			// Bind to any available local port
			_socket.Bind(new IPEndPoint(IPAddress.Any, 0));

			// Socket is ready, client is in initial state
			Utils.SetState(ref _currentState, ClientState.Start, _logger);
			_logger.LogInformation("UDP Socket bound locally to: {LocalEndPoint}", _socket.LocalEndPoint);
			_logger.LogInformation("Please authenticate using /auth <Username> <Secret> <DisplayName>"); // User guidance


			// Start the concurrent receive and user input handling tasks
			var receiveTask = ReceiveMessagesUdpAsync(_cts.Token); // Defined in Receive partial
			var sendTask = HandleUserInputUdpAsync(_cts.Token); // Defined below

			// Wait for either task to complete (e.g., due to error, cancellation, or disconnect)
			await Task.WhenAny(receiveTask, sendTask);

			_logger.LogInformation("Main UDP client loops finished.");
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
			_logger.LogInformation("UDP Client operation cancelled during startup.");
		}
		catch (SocketException ex)
		{
			_logger.LogError(ex, "Socket error during UDP client startup: {SocketErrorCode}", ex.SocketErrorCode);
			Console.WriteLine($"ERROR: Network error during startup: {ex.Message}");
			await InitiateShutdownAsync($"Socket error during startup: {ex.Message}");
			Environment.ExitCode = 1;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error during UDP client startup or operation.");
			Console.WriteLine($"ERROR: Unexpected startup error: {ex.Message}");
			await InitiateShutdownAsync($"Unexpected startup ERROR: {ex.Message}");
			Environment.ExitCode = 1;
		}
		finally
		{
			OwnDispose(); // Defined below
			Utils.SetState(ref _currentState, ClientState.End, _logger);
			_logger.LogInformation("Client StartClientAsync finished, resources disposed.");
		}
	}

	// --- User Input Handling ---

	// Handles the loop for reading user input, parsing it, and validating state.
	private async Task HandleUserInputUdpAsync(CancellationToken cancellationToken)
	{
		_logger.LogDebug("Starting user input handler loop...");
		try
		{
			// Loop while cancellation is not requested and client is not ending
			while (!cancellationToken.IsCancellationRequested && _currentState != ClientState.End)
			{
				string input = await Task.Run(() => Console.ReadLine(), cancellationToken); // ReadLine on background thread

				if (input == null || cancellationToken.IsCancellationRequested) // Ctrl+D or external cancellation
				{
					_logger.LogInformation("End of input detected or cancellation requested. Initiating graceful shutdown...");
					if (!_cts.IsCancellationRequested) // Avoid double-triggering shutdown
						await InitiateShutdownAsync("User initiated shutdown (EOF/Ctrl+D/Cancel).", true);
					break;
				}

				if (string.IsNullOrWhiteSpace(input)) continue; // Ignore empty/whitespace input

				var parsed = _userInputParser.ParseUserInput(input); // Use the injected parser

				if (parsed.Type == UserInputParser.CommandParseResultType.Unknown)
					continue;

				// --- State Validation (Original Logic) ---
				bool canProceed = true;
				switch (_currentState)
				{
					case ClientState.Start:
					case ClientState.Connected: // Allow AUTH from either Start or Connected state
						if (parsed.Type != UserInputParser.CommandParseResultType.Auth && parsed.Type != UserInputParser.CommandParseResultType.Help)
						{
							Console.WriteLine("ERROR: Must authenticate first. Use /auth <Username> <Secret> <DisplayName>");
							canProceed = false;
						}

						break;

					case ClientState.Authenticating:
						if (parsed.Type != UserInputParser.CommandParseResultType.Help) // Allow only help while authenticating
						{
							Console.WriteLine("ERROR: Please wait for authentication to complete.");
							canProceed = false;
						}

						break;

					case ClientState.Joining: // zbytocne? (Note: Original comment, keep for now)
						if (parsed.Type != UserInputParser.CommandParseResultType.Help) // Allow only help while joining
						{
							Console.WriteLine("ERROR: Please wait for join operation to complete.");
							canProceed = false;
						}

						break;

					case ClientState.Joined:
						if (parsed.Type == UserInputParser.CommandParseResultType.Auth)
						{
							Console.WriteLine("ERROR: Already authenticated.");
							canProceed = false;
						}

						// Allow ChatMessage, Join, Rename, Help in Joined state - no explicit check needed here, as other cases prevent them elsewhere
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
				// --- End State Validation ---


				// Process the valid command (send packet, update local state, etc.)
				await ProcessParsedUserInputAsync(parsed, cancellationToken); // Defined below
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation("UDP user input handler loop cancelled.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error in UDP user input handler loop.");
			if (!_cts.IsCancellationRequested)
				await InitiateShutdownAsync($"User input loop ERROR: {ex.Message}");
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

	// Processes a parsed user input command: prepares message, sends, and manages reliability expectations.
	private async Task ProcessParsedUserInputAsync(UserInputParser.ParsedUserInput parsed, CancellationToken cancellationToken) // Renamed parameter
	{
		// This method contains the UDP-specific actions for each command type (sending packets, updating state)
		switch (parsed.Type)
		{
			case UserInputParser.CommandParseResultType.Auth:
				// Check prerequisites (must be in Start or Connected state) - Redundant due to HandleUserInputUdpAsync validation, but safe
				if (_currentState != ClientState.Start && _currentState != ClientState.Connected)
				{
					_logger.LogWarning("Attempted AUTH from incorrect state after validation: {State}", _currentState);
					return; // Exit method
				}

				// Store credentials/display name for pending authentication while waiting for reply
				_pendingUsername = parsed.Username;
				_pendingSecret = parsed.Secret;
				_pendingDisplayName = parsed.DisplayName;

				// Set state *before* sending request
				Utils.SetState(ref _currentState, ClientState.Authenticating, _logger);
				_logger.LogInformation($"Attempting authentication as '{_pendingDisplayName}'...");

				// Send AUTH packet and wait for REPLY using reliable send mechanism
				await SendAuthRequestAsync(_pendingUsername, _pendingDisplayName, _pendingSecret, cancellationToken); // Defined below

				break;

			case UserInputParser.CommandParseResultType.Join:
				// Check prerequisites (must be authenticated) 
				if (_currentState != ClientState.Authenticating && _currentState != ClientState.Joined) // Can join from Authenticated or already Joined
				{
					_logger.LogWarning("Attempted JOIN from incorrect state after validation: {State}", _currentState);
					return; // Exit method
				}

				if (string.IsNullOrEmpty(_currentDisplayName)) // Also need a confirmed display name
				{
					_logger.LogWarning("Attempted JOIN without a confirmed display name ({State})", _currentState);
					Console.WriteLine("ERROR: Must authenticate successfully first to set your display name.");
					return; // Exit method
				}

				// Set state *before* sending request
				// Note: UDP JOIN might stay in Authenticated until JOIN OK REPLY
				Utils.SetState(ref _currentState, ClientState.Joining, _logger);
				_currentChannelId = parsed.ChannelId; // Store pending channel ID
				_logger.LogInformation($"Attempting to join channel '{parsed.ChannelId}'...");

				// Send JOIN packet and wait for REPLY using reliable send mechanism
				await SendJoinRequestAsync(parsed.ChannelId, _currentDisplayName, cancellationToken); // Use confirmed display name, Defined below

				break;

			case UserInputParser.CommandParseResultType.Rename:
				// /rename is handled locally in this protocol version (based on original code logic)
				// Truncate display name using static ClientMessageFormatter
				string newName = ClientMessageFormatter.Truncate(parsed.DisplayName, ProtocolValidation.MaxDisplayNameLength, "Rename DisplayName", _logger);
				// Validate truncated name using static ProtocolValidation
				if (ProtocolValidation.IsValidDisplayName(newName))
				{
					_currentDisplayName = newName; // Update local display name
					_logger.LogInformation("Local display name changed to {DisplayName}", _currentDisplayName);
					// No console output for this local change according to strict rules. Log only.
				}
				else
				{
					// Validation failed, error logged by Truncate/IsValidDisplayName. Use internal error format.
					Console.WriteLine("ERROR: Invalid display name format/characters for /rename.");
				}

				// No server message for this local command
				break;

			case UserInputParser.CommandParseResultType.Help:
				Utils.PrintHelp();
				// No server message
				break;

			case UserInputParser.CommandParseResultType.ChatMessage:
				// Check prerequisites (must be joined) - Redundant due to HandleUserInputUdpAsync validation, but safe
				if (_currentState != ClientState.Joined) // Chat requires Joined state
				{
					_logger.LogWarning("Attempted ChatMessage from incorrect state after validation: {State}", _currentState);
					return; // Exit method
				}

				if (string.IsNullOrEmpty(_currentDisplayName) || string.IsNullOrEmpty(_currentChannelId)) // Also need display name and channel
				{
					_logger.LogWarning("Attempted ChatMessage without display name or channel ({State})", _currentState);
					Console.WriteLine("ERROR: Authentication and joining a channel are required to chat.");
					return; // Exit method
				}

				// Send MSG packet and wait for CONFIRM using reliable send mechanism
				await SendMessageRequestAsync(_currentDisplayName, parsed.OriginalInput, cancellationToken); // Use confirmed display name, Defined below
				break;

			case UserInputParser.CommandParseResultType.Unknown:
				// Error message already printed by the UserInputParser.
				// No server message to send.
				break;

			// Default case for safety, though all defined types are covered above
			default:
				_logger.LogError("Unhandled ParsedUserInput type in ProcessParsedUserInputAsync: {Type}", parsed.Type);
				Console.WriteLine($"ERROR: Internal error - Unhandled command type ({parsed.Type}).");
				break;
		}
	}

	// Sends a message packet reliably, waiting for a CONFIRMATION within a timeout, with retries.
	private async Task<bool> SendReliableUdpMessageAsync(ushort messageId, byte[] messageBytes, IPEndPoint targetEndPoint, CancellationToken cancellationToken)
	{
		var confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		// Register this TCS so HandleIncomingConfirm can find it
		if (!_pendingConfirms.TryAdd(messageId, confirmTcs))
		{
			_logger.LogWarning("Concurrency issue: Failed to add pending confirm for MessageID: {MessageId}. Already waiting?", messageId);
			return false; // If this happens, it's an internal error. Treat as send failure.
		}

		_logger.LogDebug("Attempting reliable send for message ID: {MessageId} to {TargetEndPoint}, awaiting CONFIRM.", messageId, targetEndPoint);

		try
		{
			// Retry Loop (1 initial attempt + MaxRetries additional attempts)
			for (int attempt = 0; attempt <= MaxRetries; attempt++)
			{
				// Check for cancellation before each attempt and before waiting
				if (cancellationToken.IsCancellationRequested || _currentState == ClientState.End)
				{
					_logger.LogInformation("Reliable send cancelled before attempt {Attempt} for MessageID: {MessageId}.", attempt + 1, messageId);
					return false; // Cancelled
				}

				// --- Send the packet ---
				try
				{
					if (_socket == null || _currentState == ClientState.End)
					{
						_logger.LogWarning("Cannot send MessageID {MessageId}, socket is null or client is ending.", messageId);
						return false; // Cannot send if socket is invalid or shutting down
					}

					var bytesSent = await _socket.SendToAsync(messageBytes, SocketFlags.None, targetEndPoint, cancellationToken);

					_logger.LogTrace("Attempt {AttemptNum}/{TotalAttempts}: Sent message ID: {MessageId} ({NumBytes} bytes) to {TargetEndPoint}.",
						attempt + 1, MaxRetries + 1, messageId, bytesSent, targetEndPoint);
				}
				catch (OperationCanceledException)
				{
					// If cancellation happens during SendToAsync itself
					_logger.LogInformation("SendToAsync cancelled for MessageID: {MessageId}, Attempt {Attempt}.", messageId, attempt + 1);
					return false; // Cancelled
				}
				catch (SocketException se)
				{
					_logger.LogError(se, "SocketException on SendToAsync for MessageID: {MessageId}, Attempt: {Attempt}. Retrying...", messageId, attempt + 1);
					// Delay before retry on network error
					await Task.Delay(ConfirmationTimeoutMilliseconds / 2, cancellationToken);
					continue; // Go to next attempt
				}
				catch (ObjectDisposedException)
				{
					_logger.LogWarning("Attempted send on disposed socket for MessageID: {MessageId}. Aborting reliable send.", messageId);
					return false; // Cannot recover
				}
				catch (Exception ex) // Catch-all for other unexpected send errors
				{
					_logger.LogError(ex, "Unexpected exception during SendToAsync for MessageID: {MessageId}, Attempt: {Attempt}. Aborting reliable send.", messageId, attempt + 1);
					return false; // Cannot recover
				}

				// --- Wait for CONFIRM ---
				try
				{
					using var timeoutCts = new CancellationTokenSource(ConfirmationTimeoutMilliseconds); // Timeout for CONFIRM
					using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

					// Wait for either the confirm TCS to be set or the timeout/cancellation
					var completedTask = await Task.WhenAny(confirmTcs.Task, Task.Delay(-1, linkedCts.Token));

					if (completedTask == confirmTcs.Task)
					{
						// Confirm task completed - success!
						await confirmTcs.Task; // Await the result (it will be true)
						_logger.LogDebug("CONFIRM received for MessageID: {MessageId}.", messageId);
						return true; // Message confirmed
					}
					else
					{
						// Timeout or cancellation occurred while waiting for confirm
						if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
						{
							// It was a timeout
							_logger.LogWarning("Timeout waiting for CONFIRM for MessageID: {MessageId}, Attempt: {Attempt}/{MaxAttempts}. Retrying...", messageId, attempt + 1,
								MaxRetries + 1);
							// No delay needed here, the next iteration's Task.Delay handles it or we continue loop immediately
						}
						else
						{
							// It was cancelled by the main token
							_logger.LogInformation("Reliable send cancelled by main token while waiting for CONFIRM for MessageID: {MessageId}.", messageId);
							return false; // Cancelled
						}
					}
				}
				catch (OperationCanceledException)
				{
					// This catch block is primarily for Task.Delay cancellation.
					if (cancellationToken.IsCancellationRequested)
					{
						_logger.LogInformation("Reliable send cancelled by main token while waiting for CONFIRM for MessageID: {MessageId}.", messageId);
						return false; // Cancelled
					}
					else
					{
						// This means the Task.Delay cancellation happened due to timeoutCts
						_logger.LogWarning("Timeout (via cancellation) waiting for CONFIRM for MessageID: {MessageId}, Attempt: {Attempt}/{MaxAttempts}.", messageId, attempt + 1,
							MaxRetries + 1);
					}
				}
				catch (Exception ex) // Catch-all for other unexpected errors while waiting
				{
					_logger.LogError(ex, "Exception while waiting for CONFIRM Task for MessageID: {MessageId}. Aborting reliable send.", messageId);
					return false; // Cannot recover
				}
			}

			// Loop finished without receiving a CONFIRM
			_logger.LogError("Message ID: {MessageId} failed to get CONFIRM after {NumAttempts} attempts.", messageId, MaxRetries + 1);
			return false; // Failed after retries
		}
		finally
		{
			// Clean up the pending confirm entry regardless of success, failure, or cancellation
			_pendingConfirms.TryRemove(messageId, out _);
			_logger.LogTrace("Cleaned up pending confirm entry for MessageID: {MessageId}", messageId);
		}
	}

	// Sends a request packet that expects a functional REPLY, waiting first for CONFIRM, then for REPLY.
	private async Task SendRequestAndWaitForReplyAsync(ushort messageId, byte[] messageBytes, IPEndPoint endPoint, CancellationToken cancellationToken,
		string requestDescription, ClientState failureState) // requestDescription for logging, failureState to revert to on failure
	{
		_logger.LogDebug("Sending {Description} (ID: {MessageId}) to {EndPoint} and awaiting CONFIRM.", requestDescription, messageId, endPoint);

		// --- Step 1: Send reliably and wait for CONFIRM ---
		bool confirmed = await SendReliableUdpMessageAsync(messageId, messageBytes, endPoint, cancellationToken);

		if (!confirmed)
		{
			_logger.LogError("{Description} message (ID: {MessageId}) was not confirmed by the server.", requestDescription, messageId);
			Console.WriteLine($"ERROR: Server did not confirm {requestDescription} request (ID: {messageId}).");
			Utils.SetState(ref _currentState, failureState, _logger);
			_logger.LogDebug("--> {Description} attempt failed (no CONFIRM). Reverting state to {State}.", requestDescription, failureState);
			return;
		}

		_logger.LogInformation("{Description} message (ID: {MessageId}) confirmed by server. Now waiting for functional REPLY...", requestDescription, messageId);


		// --- Step 2: Wait for functional REPLY ---
		var replyTcs = new TaskCompletionSource<ParsedServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
		// Register this TCS so ParseAndHandleReply can find it using the original request ID
		if (!_pendingReplies.TryAdd(messageId, replyTcs))
		{
			_logger.LogWarning("Failed to add pending reply handler for {Description} MessageID: {MessageId}. Aborting wait.", requestDescription, messageId);
			// Revert state on failure to register
			Utils.SetState(ref _currentState, failureState, _logger);
			return;
		}

		var localReplyTcs = replyTcs; // Use local variable

		try
		{
			using var replyTimeoutCts = new CancellationTokenSource(ReplyTimeoutMilliseconds); // Timeout for REPLY
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, replyTimeoutCts.Token);

			// Wait for either the reply TCS to be set or the timeout/cancellation
			var completedTask = await Task.WhenAny(localReplyTcs.Task, Task.Delay(ReplyTimeoutMilliseconds, linkedCts.Token));

			if (completedTask == localReplyTcs.Task)
			{
				// Reply task completed - success!
				ParsedServerMessage parsedReply = await localReplyTcs.Task; // Await the result (the ParsedServerMessage)
				_logger.LogDebug("REPLY received for {Description} (ID: {MessageId}). Processing functional reply...", requestDescription, messageId);
				ProcessFunctionalReply(parsedReply); // Call the generic handler to update state based on OK/NOK (Defined in Receive partial)
			}
			else
			{
				// Timeout or cancellation occurred while waiting for reply
				if (replyTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
				{
					// It was a timeout
					_logger.LogError("Timeout waiting for REPLY to {Description} message (ID: {MessageId}).", requestDescription, messageId);
					Console.WriteLine($"ERROR: Server did not reply to {requestDescription} request (ID: {messageId}) within the timeout period.");
					// Revert state on timeout
					Utils.SetState(ref _currentState, failureState, _logger);
					_logger.LogDebug("--> {Description} attempt failed (timeout). Reverting state to {State}.", requestDescription, failureState);
				}
				else
				{
					// It was cancelled by the main token
					_logger.LogInformation("Operation cancelled by main token while waiting for {Description} REPLY (ID: {MessageId}). Shutdown likely.", requestDescription,
						messageId);
					// State will be handled by the main shutdown logic
				}
			}
		}
		catch (OperationCanceledException)
		{
			// This catch block is primarily for Task.Delay cancellation.
			if (cancellationToken.IsCancellationRequested)
			{
				_logger.LogInformation("Operation cancelled by main token while waiting for {Description} REPLY (ID: {MessageId}). Shutdown likely.", requestDescription,
					messageId);
				// State will be handled by the main shutdown logic
			}
			else
			{
				// This means the Task.Delay cancellation happened due to replyTimeoutCts
				_logger.LogError("Timeout (via cancellation) waiting for REPLY to {Description} message (ID: {MessageId}).", requestDescription, messageId);
				Console.WriteLine($"ERROR: Server did not reply to {requestDescription} request (ID: {messageId}) within the timeout period.");
				// Revert state on timeout
				Utils.SetState(ref _currentState, failureState, _logger);
				_logger.LogDebug("--> {Description} attempt failed (timeout). Reverting state to {State}.", requestDescription, failureState);
			}
		}
		catch (Exception ex) // Catch-all for other unexpected errors while waiting
		{
			_logger.LogError(ex, "Exception occurred while awaiting {Description} REPLY task (ID: {MessageId}).", requestDescription, messageId);
			Console.WriteLine($"ERROR: An unexpected error occurred while waiting for {requestDescription} reply (ID: {messageId}): {ex.Message}");
			// Revert state on error
			Utils.SetState(ref _currentState, failureState, _logger);
		}
		finally
		{
			// Clean up the pending reply entry regardless of success, failure, or cancellation
			_pendingReplies.TryRemove(messageId, out _);
			_logger.LogTrace("Cleaned up pending reply entry for {Description} message ID: {MessageId}", requestDescription, messageId);
		}
	}

	// Sends an AUTH packet using the reliable request-reply pattern.
	private async Task SendAuthRequestAsync(string username, string displayName, string secret, CancellationToken cancellationToken)
	{
		// Check state before sending - Redundant due to ProcessParsedUserInputAsync validation, but safe
		if (_currentState != ClientState.Authenticating)
		{
			_logger.LogWarning("SendAuthRequestAsync called unexpectedly when not in Authenticating state ({State}). Aborting.", _currentState);
			return;
		}

		_logger.LogInformation("Executing authentication request as User:'{Username}', Display:'{DisplayName}'", username, displayName);

		// Get next message ID and format the packet
		ushort messageId = _nextMessageId++;
		byte[] authMessageBytes = UdpMessageFormat.FormatAuthManually(messageId, username, displayName, secret); // Assumes UdpMessageFormat static class

		if (authMessageBytes == null)
		{
			_logger.LogError("Critical: Failed to format AUTH message (ID: {MessageId}).", messageId);
			Console.WriteLine("ERROR: Internal error formatting authentication message.");
			// Revert state on fatal formatting error
			Utils.SetState(ref _currentState, ClientState.Start, _logger);
			// Clear pending credentials as AUTH failed
			_pendingUsername = null;
			_pendingSecret = null;
			_pendingDisplayName = null;
			return; // Exit method
		}

		// Send the request and wait for the functional REPLY
		await SendRequestAndWaitForReplyAsync(messageId, authMessageBytes, _currentServerEndPoint, cancellationToken,
			"AUTH", ClientState.Start // Revert to Start state if AUTH fails/times out
		);

		_logger.LogDebug("SendAuthRequestAsync finished for ID {MessageId}.", messageId);
	}

	// Sends a JOIN packet using the reliable request-reply pattern.
	private async Task SendJoinRequestAsync(string channelId, string displayName, CancellationToken cancellationToken)
	{
		// Check state before sending - Redundant due to ProcessParsedUserInputAsync validation, but safe
		if (_currentState != ClientState.Joining)
		{
			_logger.LogWarning("SendJoinRequestAsync called when not in Joining state ({State}). Aborting.", _currentState);
			// If called incorrectly, revert state. Need to decide the *previous* state here. Authenticated?
			Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Assume Authenticated was the state before attempting JOIN
			return;
		}

		_logger.LogInformation("Executing join channel request: Channel:'{ChannelId}', As:'{DisplayName}'", channelId, displayName);

		// Get next message ID and format the packet
		ushort messageId = _nextMessageId++;
		byte[] joinBytes = UdpMessageFormat.FormatJoinManually(messageId, channelId, displayName); // Assumes UdpMessageFormat static class

		if (joinBytes == null)
		{
			_logger.LogError("Critical: Failed to format JOIN message (ID: {MessageId}).", messageId);
			Console.WriteLine("ERROR: Internal error formatting join message.");
			// Revert state on fatal formatting error
			Utils.SetState(ref _currentState, ClientState.Authenticating, _logger); // Assuming Authenticated was state before attempt
			return; // Exit method
		}

		// Send the request and wait for the functional REPLY
		await SendRequestAndWaitForReplyAsync(messageId, joinBytes, _currentServerEndPoint,
			cancellationToken, "JOIN", ClientState.Authenticating // Revert to Authenticated state if JOIN fails/times out
		);

		_logger.LogDebug("SendJoinRequestAsync finished for ID {MessageId}.", messageId);
	}

	// Sends a MSG packet using the reliable request-confirm pattern.
	private async Task SendMessageRequestAsync(string displayName, string messageContent, CancellationToken cancellationToken)
	{
		// Check state before sending - Redundant due to ProcessParsedUserInputAsync validation, but safe
		if (_currentState != ClientState.Joined)
		{
			_logger.LogWarning("SendMessageRequestAsync called when not in Joined state ({State}). Message not sent.", _currentState);
			Console.WriteLine("ERROR: Cannot send message - Not currently joined to a channel.");
			return;
		}

		_logger.LogDebug("Preparing to send MSG: From='{DisplayName}', Content='{Content}'", displayName, messageContent);

		// Get next message ID and format the packet
		ushort messageId = _nextMessageId++;
		byte[] msgBytes = UdpMessageFormat.FormatMsgManually(messageId, displayName, messageContent);

		if (msgBytes == null)
		{
			_logger.LogError("Critical: Failed to format MSG message (ID: {MessageId}).", messageId);
			Console.WriteLine("ERROR: Internal error formatting chat message.");
			return; // Exit method
		}

		_logger.LogDebug("Sending MSG (ID: {MessageId}) to {EndPoint} and awaiting CONFIRM.", messageId, _currentServerEndPoint);

		// Send reliably, waiting only for CONFIRM (not REPLY)
		bool confirmed = await SendReliableUdpMessageAsync(messageId, msgBytes, _currentServerEndPoint, cancellationToken);

		if (!confirmed)
		{
			_logger.LogError("MSG message (ID: {MessageId}) was not confirmed by the server after retries.", messageId);
			Console.WriteLine($"ERROR: Server did not confirm receipt of your message (ID: {messageId}). It might not have been delivered.");
			// No state change needed for unconfirmed chat message (usually)
		}
		else
		{
			_logger.LogInformation("MSG message (ID: {MessageId}) successfully confirmed by server.", messageId);
			// No console output needed for successful confirmation according to rules. Log only.
		}
	}

	// Sends an ERR message packet. This is typically non-reliable fire-and-forget from client side.
	private async Task SendErrorAsync(string errorMessage, IPEndPoint targetEndPoint)
	{
		// Precondition: Need a display name to format the ERR message correctly.
		// Use a default like "Client" if not authenticated yet.
		string errDisplayName = string.IsNullOrEmpty(_currentDisplayName) ? "Client" : _currentDisplayName;

		// Get next message ID and format the packet
		ushort messageId = _nextMessageId++;
		byte[] errBytes = UdpMessageFormat.FormatErrManually(messageId, errDisplayName, errorMessage); // Assumes UdpMessageFormat static class

		// Check if formatting was successful.
		if (errBytes == null)
		{
			_logger.LogError("Failed to format ERR message (ID: {MessageId}) for content: {Content}. Message not sent.", messageId, errorMessage);
			Console.WriteLine("ERROR: Internal error formatting outgoing ERR message.");
			return; // Exit method
		}

		_logger.LogInformation("Sending ERR (ID: {MessageId}) to {TargetEndPoint}: {ErrorMessage}", messageId, targetEndPoint, errorMessage);

		try
		{
			// Send the packet fire-and-forget (no confirm expected for client-sent ERR)
			// Check socket and state before sending
			if (_socket != null && _currentState != ClientState.End)
				await _socket.SendToAsync(errBytes, SocketFlags.None, targetEndPoint); // No cancellation token typically for fire-and-forget
			else
				_logger.LogWarning("Cannot send ERR (ID: {MessageId}) because socket is null or client is shutting down.", messageId);
		}
		catch (ObjectDisposedException)
		{
			_logger.LogWarning("Attempted to send ERR (ID: {MessageId}) on a disposed socket during shutdown.", messageId);
		}
		catch (SocketException se)
		{
			// Log network errors during send. Non-fatal for this fire-and-forget message.
			_logger.LogWarning(se, "SocketException (Code:{SocketErrorCode}) while sending ERR (ID: {MessageId}) to {TargetEndPoint}. Message likely not sent.",
				se.SocketErrorCode, messageId, targetEndPoint);
		}
		catch (Exception ex) // Catch-all for other unexpected errors
		{
			_logger.LogError(ex, "Unexpected exception while sending ERR (ID: {MessageId}) to {TargetEndPoint}.", messageId, targetEndPoint);
		}
	}

	// Initiates a graceful shutdown sequence for the client.
	// isClientInitiatedEof is true if triggered by user input EOF (Ctrl+C/D), false otherwise (server BYE, error, etc.).
	private async Task InitiateShutdownAsync(string reason, bool isClientInitiatedEof = false)
	{
		// Use a flag to prevent re-entrancy more reliably than just checking _cts
		if (Interlocked.CompareExchange(ref _shutdownInitiatedFlag, 1, 0) != 0)
		{
			_logger.LogDebug("Shutdown already initiated or in progress. Reason: {Reason}", reason);
			return;
		}


		_logger.LogInformation("Initiating graceful shutdown. Reason: {Reason}", reason);
		Console.WriteLine($"ERROR: Client shutting down ({reason}).");

		ClientState stateBeforeShutdown = _currentState;

		// --- Keep main cancellation for stopping loops/long waits ---
		// We still need to signal the main loops to stop eventually.
		// But we do it *after* attempting the reliable BYE send.

		Task reliableByeTask = Task.CompletedTask; // Task to wait for

		if (isClientInitiatedEof && _socket != null && _currentServerEndPoint != null &&
		    (stateBeforeShutdown == ClientState.Authenticating || stateBeforeShutdown == ClientState.Joined || stateBeforeShutdown == ClientState.Joining))
		{
			ushort byeId = _nextMessageId++;
			string byeDisplayName = string.IsNullOrEmpty(_currentDisplayName) ? "Client" : _currentDisplayName;
			byte[] byeBytes = UdpMessageFormat.FormatByeManually(byeId, byeDisplayName);

			if (byeBytes != null)
			{
				_logger.LogInformation("Attempting to send BYE (ID: {ByeId}) message to {TargetEndPoint} and wait for CONFIRM...", byeId, _currentServerEndPoint);

				using var reliableByeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

				// Let SendReliableUdpMessageAsync manage its own retries/timeouts based on the token we give it.
				reliableByeTask = SendReliableUdpMessageAsync(byeId, byeBytes, _currentServerEndPoint, reliableByeTimeoutCts.Token);

			}
			else
			{
				_logger.LogWarning("Failed to format BYE message during shutdown.");
			}
		}

		// --- Now, wait for the reliable BYE send attempt to finish ---
		try
		{
			await reliableByeTask; // Wait for the SendReliableUdpMessageAsync task to complete

			// Check the result if needed (the variable 'confirmed' logic from previous example)
			// This requires SendReliableUdpMessageAsync to be captured differently or have its result checked
			// Simpler: Just log based on task status if needed, or assume best effort completed.
			if (reliableByeTask.IsCompletedSuccessfully)
			{
				// You'd need to modify SendReliable to return info or check the bool result differently
				_logger.LogInformation("Reliable BYE send task completed (check logs for actual confirmation).");
			}
			else if (reliableByeTask.IsCanceled)
			{
				_logger.LogWarning("Reliable BYE send task was cancelled (likely timed out).");
			}
			else if (reliableByeTask.IsFaulted)
			{
				_logger.LogError(reliableByeTask.Exception?.GetBaseException(), "Reliable BYE send task failed.");
			}
		}
		catch (Exception ex)
		{
			// Catch potential exceptions from the await itself if the task failed badly
			_logger.LogError(ex, "Exception awaiting reliable BYE send task.");
		}

		// --- Now Signal Main Cancellation ---
		_logger.LogDebug("Signalling main cancellation token.");
		_cts?.Cancel();

		// --- Set Final State ---
		// State is set to End AFTER attempting BYE and signalling cancellation.
		Utils.SetState(ref _currentState, ClientState.End, _logger);

		// --- Cancel Pending Operations (that might have been started before cancellation signal) ---
		// This is somewhat redundant if main loops check cancellation properly, but safe.
		CancelAllPendingOperations($"Shutdown initiated: {reason}");

		// --- Short delay AFTER main cancellation to allow loops to potentially exit ---
		await Task.Delay(100, CancellationToken.None); // Increased delay slightly

		// --- Dispose resources ---
		OwnDispose();
	}

	// Cancels all pending reliable send operations and clears tracking dictionaries.
	private void CancelAllPendingOperations(string reason)
	{
		_logger.LogDebug("Cancelling all pending operations due to: {Reason}", reason);

		// Cancel all pending CONFIRM TaskCompletionSources
		var confirmKeys = _pendingConfirms.Keys.ToList();
		foreach (var key in confirmKeys)
		{
			if (_pendingConfirms.TryRemove(key, out var tcs))
			{
				// Use TrySetCanceled to signal the tasks are cancelled
				tcs.TrySetCanceled(_cts.Token); // Pass the main token if relevant
				_logger.LogTrace("Cancelled pending confirm TCS for MessageID: {MessageId}", key);
			}
		}

		_pendingConfirms.Clear(); // Ensure dictionary is empty

		// Cancel all pending REPLY TaskCompletionSources
		var replyKeys = _pendingReplies.Keys.ToList();
		foreach (var key in replyKeys)
		{
			if (_pendingReplies.TryRemove(key, out var tcs))
			{
				// Use TrySetCanceled to signal the tasks are cancelled
				tcs.TrySetCanceled(_cts.Token); // Pass the main token if relevant
				_logger.LogTrace("Cancelled pending reply TCS for MessageID: {MessageId}", key);
			}
		}

		_pendingReplies.Clear(); // Ensure dictionary is empty

		// Clear the processed incoming message IDs cache (optional, but good cleanup)
		_processedIncomingMessageIds.Clear(); // Used in the receive partial
	}


	// Disposes of client resources (socket, cancellation token sources, wait handle).
	// Called in the finally block of StartClientAsync and by InitiateShutdownAsync.
	public void OwnDispose() // Not implementing IDisposable, but a manual cleanup method
	{
		_logger.LogDebug("OwnDispose executing...");

		// Set state to End early in dispose process
		Utils.SetState(ref _currentState, ClientState.End, _logger);

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


		// Clear dictionaries - TCSs should already be cancelled by CancelAllPendingOperations
		_pendingConfirms?.Clear();
		_pendingReplies?.Clear();
		_processedIncomingMessageIds?.Clear();
		_logger.LogDebug("Cleared pending confirms/replies dictionaries and processed incoming IDs.");


		// Dispose the Socket
		var socketToDispose = _socket;
		_socket = null; // Nullify member variable FIRST

		if (socketToDispose != null)
		{
			_logger.LogDebug("Disposing socket...");
			try
			{
				// Close the connection and release the handle for UDP
				socketToDispose.Close(); // implicitly Disposes()
				_logger.LogDebug("Socket Close called.");
			}
			catch (Exception ex) // Catch errors during Close
			{
				_logger.LogWarning(ex, "Exception during socket Close.");
			}
		}
		else
		{
			_logger.LogDebug("Socket was already null during dispose.");
		}

		_logger.LogDebug("OwnDispose finished.");
	}
}
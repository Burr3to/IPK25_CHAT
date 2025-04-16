using IPK25_CHAT.Udp;

namespace IPK25_CHAT;

class Program
{
	static async Task Main(string[] args)
	{
		// 1. Configure Logging (Simplified)
		using var loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddSimpleConsole(options =>
			{
				options.SingleLine = true;
				options.TimestampFormat = "HH:mm:ss ";
			});
			builder.SetMinimumLevel(LogLevel.Debug);
		});
		ILogger<Program> logger = loggerFactory.CreateLogger<Program>();

		// 2. Parse Command-Line Arguments
		var parsedOptions = ArgumentParser.ParseArguments(args);

		if (parsedOptions == null)
		{
			logger.LogError("Failed to parse command-line arguments.");
			return;
		}

		// 3. Create and Run the Client
		if (parsedOptions.Transport.ToLower() == "tcp")
		{
			var messages = new Messages(loggerFactory.CreateLogger<Messages>());
			var tcpClient = new TcpChatClient(loggerFactory.CreateLogger<TcpChatClient>(), messages);
			await tcpClient.StartClientAsync(parsedOptions);
		}
		else if (parsedOptions.Transport.ToLower() == "udp")
		{
			var messages = new Messages(loggerFactory.CreateLogger<Messages>());
			var udpClient = new UdpChatClient(loggerFactory.CreateLogger<UdpChatClient>(), messages);
			await udpClient.StartClientAsync(parsedOptions);
			return;
		}
		else
		{
			logger.LogError("Invalid transport protocol. Use 'tcp' or 'udp'.");
			return;
		}

		logger.LogInformation("Application ended.");
	}
}
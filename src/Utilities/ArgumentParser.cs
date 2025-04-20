namespace IPK25_CHAT;

public class ArgumentParser
{
	public class Options
	{
		[Option('t', "transport", Required = true, HelpText = "Transport protocol used for connection (tcp or udp).")]
		public string Transport { get; set; }

		[Option('s', "server", Required = true, HelpText = "Server IP or hostname.")]
		public string Server { get; set; }

		[Option('p', "port", Default = (ushort)4567, HelpText = "Server port.")]
		public ushort Port { get; set; }

		[Option('d', "udp-timeout", Default = (ushort)250, HelpText = "UDP confirmation timeout (in milliseconds).")]
		public ushort UdpTimeout { get; set; }

		[Option('r', "udp-retries", Default = (byte)3, HelpText = "Maximum number of UDP retransmissions.")]
		public byte UdpRetries { get; set; }

		[Option('h', "help", HelpText = "Prints program help output and exits.")]
		public bool Help { get; set; }
	}

	public static Options ParseArguments(string[] args)
	{
		var result = Parser.Default.ParseArguments<Options>(args);

		if (result is Parsed<Options> parsed)
		{
			if (parsed.Value.Help)
			{
				Console.WriteLine(HelpText.AutoBuild(result));
				return null;
			}

			return parsed.Value;
		}
		else
		{
			Console.WriteLine(HelpText.AutoBuild(result));
			return null;
		}
	}
}
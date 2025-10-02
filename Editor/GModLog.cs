using System;

internal static class GModLog
{
	// Toggle detailed debug logs by setting environment variable GMOD_MOUNT_DEBUG=1
	private static bool _enabled = string.Equals(Environment.GetEnvironmentVariable("GMOD_MOUNT_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);

	public static bool Enabled
	{
		get => _enabled;
		set => _enabled = value;
	}

	public static void Debug(string message)
	{
		if (!_enabled) return;
		//Log.Info($"[gmod] {message}");
	}

	public static void Info(string message)
	{
		//Log.Info($"[gmod] {message}");
	}

	public static void Warn(string message)
	{
		//Log.Warning($"[gmod] {message}");
	}

	public static void Error(string message)
	{
		//Log.Error($"[gmod] {message}");
	}
}



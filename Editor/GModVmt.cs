using System;
using System.Collections.Generic;
using IO = System.IO;

internal static class GModVmt
{
	public sealed class Data
	{
		public string Shader;
		public Dictionary<string,string> Kv = new(StringComparer.OrdinalIgnoreCase);
	}

	public static Data Parse( IO.Stream stream )
	{
		var data = new Data();
		using var sr = new IO.StreamReader( stream );
		string line;
		while ( (line = sr.ReadLine()) != null )
		{
			line = line.Trim();
			if (line.Length == 0) continue;
			if (line.StartsWith("//")) continue;
			if (line.StartsWith("\"") && data.Shader == null)
			{
				int q = line.IndexOf('"', 1);
				if (q > 1) data.Shader = line.Substring(1, q-1);
				continue;
			}
			// simplistic key "value" pairs
			if (line.StartsWith("\""))
			{
				int q1 = line.IndexOf('"', 1);
				if (q1 > 1)
				{
					int q2s = line.IndexOf('"', q1+1);
					int q2e = line.IndexOf('"', q2s+1);
					if (q2s >= 0 && q2e > q2s)
					{
						var k = line.Substring(1, q1-1);
						var v = line.Substring(q2s+1, q2e - (q2s+1));
						data.Kv[k] = v;
					}
				}
			}
		}
		return data;
	}
}



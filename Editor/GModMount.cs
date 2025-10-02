using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Mounting;
using IO = System.IO;

public class GModMount : BaseGameMount
{
	private const long AppId = 4000; // Garry's Mod

	private readonly List<string> _searchRoots = new();
	private readonly Dictionary<string, string> _mdlFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _vtfFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _vmtFiles = new(StringComparer.OrdinalIgnoreCase);

	public override string Ident => "gmod";
	public override string Title => "Garry's Mod";

	protected override void Initialize(InitializeContext context)
	{
		try
		{
			if (!context.IsAppInstalled(AppId))
			{
				GModLog.Debug($"Steam app {AppId} not installed.");
				return;
			}

			var root = context.GetAppDirectory(AppId);
			if (string.IsNullOrWhiteSpace(root) || !IO.Directory.Exists(root))
			{
				GModLog.Warn("GMod root directory not found.");
				return;
			}

			// Primary search roots (game folder and common subfolders)
			_addIfExists(_searchRoots, root);
			_addIfExists(_searchRoots, IO.Path.Combine(root, "garrysmod"));
			_addIfExists(_searchRoots, IO.Path.Combine(root, "garrysmod", "addons"));

			// Discover .mdl / .vtf files up front to avoid heavy scanning during mount
			foreach (var r in _searchRoots)
			{
				try
				{
					foreach (var mdl in IO.Directory.EnumerateFiles(r, "*.mdl", IO.SearchOption.AllDirectories))
					{
						var rel = IO.Path.GetRelativePath(root, mdl).Replace('\\', '/');
						_mdlFiles[rel] = mdl;
					}
					foreach (var vtf in IO.Directory.EnumerateFiles(r, "*.vtf", IO.SearchOption.AllDirectories))
					{
						var rel = IO.Path.GetRelativePath(root, vtf).Replace('\\', '/');
						_vtfFiles[rel] = vtf;
					}
					foreach (var vmt in IO.Directory.EnumerateFiles(r, "*.vmt", IO.SearchOption.AllDirectories))
					{
						var rel = IO.Path.GetRelativePath(root, vmt).Replace('\\', '/');
						_vmtFiles[rel] = vmt;
					}
				}
				catch (Exception ex)
				{
					GModLog.Debug($"Scan error in '{r}': {ex.Message}");
				}
			}

			IsInstalled = _mdlFiles.Count > 0 || _vtfFiles.Count > 0 || _vmtFiles.Count > 0;
			GModLog.Info($"Found {_mdlFiles.Count} .mdl, {_vtfFiles.Count} .vtf, {_vmtFiles.Count} .vmt files.");
		}
		catch (Exception ex)
		{
			GModLog.Error($"Initialize failed: {ex.Message}");
		}

		static void _addIfExists(List<string> list, string path)
		{
			if (IO.Directory.Exists(path)) list.Add(path);
		}
	}

	protected override Task Mount(MountContext context)
	{
		try
		{
			foreach (var kv in _mdlFiles)
			{
				var rel = kv.Key; // relative to app root
				var full = kv.Value;
				context.Add(ResourceType.Model, rel.Replace('\\','/'), new GModSourceModel(full));
			}
			foreach (var kv in _vtfFiles)
			{
				var rel = kv.Key;
				context.Add(ResourceType.Texture, rel.Replace('\\','/'), new GModVtfTextureLoader(rel));
			}
			foreach (var kv in _vmtFiles)
			{
				var rel = kv.Key;
				context.Add(ResourceType.Material, rel.Replace('\\','/'), new GModVmtMaterialLoader(rel));
				GModLog.Info($"Mounted material: {rel}");
			}
			IsMounted = true;
			GModLog.Info($"Mounted {_mdlFiles.Count} .mdl and {_vtfFiles.Count} .vtf entries.");
		}
		catch (Exception ex)
		{
			GModLog.Error($"Mount failed: {ex.Message}");
		}
		return Task.CompletedTask;
	}

	public IO.Stream GetFileStreamForVtf( string relPath )
	{
		try
		{
			if ( _vtfFiles.TryGetValue( relPath, out var full ) && IO.File.Exists( full ) )
			{
				return IO.File.OpenRead( full );
			}
		}
		catch { }
		return IO.Stream.Null;
	}

	public IO.Stream GetFileStreamForVmt( string relPath )
	{
		try
		{
			if ( _vmtFiles.TryGetValue( relPath, out var full ) && IO.File.Exists( full ) )
			{
				return IO.File.OpenRead( full );
			}
		}
		catch { }
		return IO.Stream.Null;
	}

	private static string NormalizePath( string s )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return string.Empty;
		s = s.Replace('\\', '/');
		return s.TrimStart('/');
	}

	public bool TryFindVmtByName( string materialName, out string relVmtPath )
	{
		relVmtPath = null;
		if ( string.IsNullOrWhiteSpace( materialName ) ) return false;
		var name = NormalizePath( materialName );
		if (name.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
		// Common case: materials/<name>.vmt under garrysmod
		var candidate = $"garrysmod/materials/{name}.vmt";
		if ( _vmtFiles.ContainsKey( candidate ) ) { relVmtPath = candidate; return true; }
		// Fallback: any VMT whose file name matches
		var fname = IO.Path.GetFileNameWithoutExtension( name );
		foreach ( var kv in _vmtFiles )
		{
			if ( string.Equals( IO.Path.GetFileNameWithoutExtension(kv.Key), fname, StringComparison.OrdinalIgnoreCase) )
			{
				relVmtPath = kv.Key;
				return true;
			}
		}
		return false;
	}

	public bool TryResolveVtfByBasetexture( string basetexture, out string relVtfPath )
	{
		relVtfPath = null;
		if ( string.IsNullOrWhiteSpace( basetexture ) ) return false;
		var p = NormalizePath( basetexture );
		if (p.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
		var candidate = $"garrysmod/materials/{p}.vtf";
		if ( _vtfFiles.ContainsKey( candidate ) ) { relVtfPath = candidate; return true; }
		// Fallback: search by filename
		var fname = IO.Path.GetFileNameWithoutExtension( p );
		foreach ( var kv in _vtfFiles )
		{
			if ( string.Equals( IO.Path.GetFileNameWithoutExtension(kv.Key), fname, StringComparison.OrdinalIgnoreCase ) )
			{
				relVtfPath = kv.Key;
				return true;
			}
		}
		return false;
	}
}



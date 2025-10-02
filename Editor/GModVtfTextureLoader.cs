using System;
using System.Collections.Generic;
using IO = System.IO;
using Sandbox.Mounting;

internal sealed class GModVtfTextureLoader : ResourceLoader<GModMount>
{
	public string RelativePath { get; }

	public GModVtfTextureLoader( string relativePath )
	{
		RelativePath = relativePath;
	}

	protected override object Load()
	{
		Log.Info($"[gmod vtf] Loading '{RelativePath}' as '{base.Path}'");
		using var stream = ((GModMount)base.Host).GetFileStreamForVtf( RelativePath );
		if ( stream == IO.Stream.Null )
		{
			Log.Warning($"[gmod vtf] stream null for '{RelativePath}'");
			return Texture.White;
		}

		using var br = new IO.BinaryReader( stream );
		var magic = br.ReadBytes( 4 );
		if ( magic[0] != 'V' || magic[1] != 'T' || magic[2] != 'F' )
		{
			Log.Warning($"[gmod vtf] bad magic for '{RelativePath}'");
			return Texture.White;
		}

		int major = br.ReadInt32();
		int minor = br.ReadInt32();
		int headerSize = br.ReadInt32();
		int width = br.ReadUInt16();
		int height = br.ReadUInt16();
		int flags = br.ReadInt32();
		ushort frames = br.ReadUInt16();
		ushort firstFrame = br.ReadUInt16();
		br.ReadBytes( 4 ); // padding
		float reflectivityX = br.ReadSingle();
		float reflectivityY = br.ReadSingle();
		float reflectivityZ = br.ReadSingle();
		br.ReadBytes( 4 ); // padding
		float bumpScale = br.ReadSingle();
		int highResImageFormat = br.ReadInt32();
		byte mipCount = br.ReadByte();
		int lowResImageFormat = br.ReadInt32();
		byte lowResWidth = br.ReadByte();
		byte lowResHeight = br.ReadByte();
		// Some versions include depth (ushort) here; we don't need it to locate top mip
		Log.Info($"[gmod vtf] v{major}.{minor} {width}x{height} fmt={(VtfFormat)highResImageFormat} header={headerSize} frames={frames} mips={mipCount} lowres={lowResWidth}x{lowResHeight} {(VtfFormat)lowResImageFormat}");
		if ( width <= 0 || height <= 0 )
		{
			Log.Warning($"[gmod vtf] invalid size {width}x{height}");
			return Texture.White;
		}

		// High-res image data starts after header + low-res thumbnail (if present),
		// and mipmaps are stored from smallest -> largest. Skip to top mip.
		long highResOffset = headerSize;
		int lowResBytes = CalcImageDataSize( (VtfFormat)lowResImageFormat, lowResWidth, lowResHeight );
		highResOffset += lowResBytes;
		int mips = Math.Max( 1, (int)mipCount );
		for ( int i = mips - 1; i >= 1; i-- )
		{
			int mw = Math.Max( 1, width >> i );
			int mh = Math.Max( 1, height >> i );
			highResOffset += CalcImageDataSize( (VtfFormat)highResImageFormat, mw, mh );
		}
		stream.Seek( highResOffset, IO.SeekOrigin.Begin );
		byte[] rgba = DecodeVTF( br, width, height, (VtfFormat)highResImageFormat );
		if ( rgba == null )
		{
			Log.Warning($"[gmod vtf] unsupported or failed decode: fmt={(VtfFormat)highResImageFormat}");
			return Texture.White;
		}

		var tex = Texture.Create( width, height ).WithMips().WithData( rgba ).Finish();
		Log.Info($"[gmod vtf] created texture {width}x{height}");
		return tex;
	}

	public static Texture LoadTextureFromMount( GModMount host, string relVtf )
	{
		try
		{
			using var stream = host.GetFileStreamForVtf( relVtf );
			if ( stream == IO.Stream.Null ) return Texture.White;
			using var br = new IO.BinaryReader( stream );
			var magic = br.ReadBytes(4);
			if ( magic[0] != 'V' || magic[1] != 'T' || magic[2] != 'F' ) return Texture.White;

			int major = br.ReadInt32();
			int minor = br.ReadInt32();
			int headerSize = br.ReadInt32();
			int width = br.ReadUInt16();
			int height = br.ReadUInt16();
			br.ReadInt32(); // flags
			br.ReadUInt16(); // frames
			br.ReadUInt16(); // firstFrame
			br.ReadBytes(4);
			br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
			br.ReadBytes(4);
			br.ReadSingle(); // bumpScale
			int highResImageFormat = br.ReadInt32();
			byte mipCount = br.ReadByte();
			int lowResImageFormat = br.ReadInt32();
			byte lowResWidth = br.ReadByte();
			byte lowResHeight = br.ReadByte();

			long highResOffset = headerSize + CalcImageDataSize((VtfFormat)lowResImageFormat, lowResWidth, lowResHeight);
			int mips = Math.Max( 1, (int)mipCount );
			for ( int i = mips - 1; i >= 1; i-- )
			{
				int mw = Math.Max( 1, width >> i );
				int mh = Math.Max( 1, height >> i );
				highResOffset += CalcImageDataSize( (VtfFormat)highResImageFormat, mw, mh );
			}
			stream.Seek( highResOffset, IO.SeekOrigin.Begin );
			var rgba = DecodeVTF( br, width, height, (VtfFormat)highResImageFormat );
			if ( rgba == null ) return Texture.White;
			return Texture.Create( width, height ).WithMips().WithData( rgba ).Finish();
		}
		catch { return Texture.White; }
	}

	private static byte[] DecodeVTF( IO.BinaryReader br, int width, int height, VtfFormat format )
	{
		int pixelCount = width * height;
		switch ( format )
		{
			case VtfFormat.RGBA8888:
			{
				var data = br.ReadBytes( pixelCount * 4 );
				return data;
			}
			case VtfFormat.ABGR8888:
			{
				var src = br.ReadBytes( pixelCount * 4 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 4, j += 4 )
				{
					byte a = src[i + 0];
					byte b = src[i + 1];
					byte g = src[i + 2];
					byte r = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}
			case VtfFormat.BGRA8888:
			{
				var src = br.ReadBytes( pixelCount * 4 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 4, j += 4 )
				{
					byte b = src[i + 0];
					byte g = src[i + 1];
					byte r = src[i + 2];
					byte a = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}
			case VtfFormat.ARGB8888:
			{
				var src = br.ReadBytes( pixelCount * 4 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 4, j += 4 )
				{
					byte a = src[i + 0];
					byte r = src[i + 1];
					byte g = src[i + 2];
					byte b = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}
			case VtfFormat.BGR888:
			{
				var src = br.ReadBytes( pixelCount * 3 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 3, j += 4 )
				{
					byte b = src[i + 0];
					byte g = src[i + 1];
					byte r = src[i + 2];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = 255;
				}
				return dst;
			}
			case VtfFormat.RGB888:
			{
				var src = br.ReadBytes( pixelCount * 3 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 3, j += 4 )
				{
					byte r = src[i + 0];
					byte g = src[i + 1];
					byte b = src[i + 2];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = 255;
				}
				return dst;
			}
			case VtfFormat.BGRX8888:
			{
				var src = br.ReadBytes( pixelCount * 4 );
				var dst = new byte[pixelCount * 4];
				for ( int i = 0, j = 0; i < src.Length; i += 4, j += 4 )
				{
					byte b = src[i + 0];
					byte g = src[i + 1];
					byte r = src[i + 2];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = 255;
				}
				return dst;
			}
			case VtfFormat.DXT1:
				return DecompressDxt1( br, width, height );
			case VtfFormat.DXT1_ONEBITALPHA:
				return DecompressDxt1( br, width, height );
			case VtfFormat.DXT3:
				return DecompressDxt3( br, width, height );
			case VtfFormat.DXT5:
				return DecompressDxt5( br, width, height );
			default:
				return null;
		}
	}

	private static int CalcImageDataSize( VtfFormat format, int width, int height )
	{
		if ( width <= 0 || height <= 0 ) return 0;
		switch ( format )
		{
			case VtfFormat.RGBA8888:
			case VtfFormat.ABGR8888:
			case VtfFormat.BGRA8888:
			case VtfFormat.ARGB8888:
			case VtfFormat.BGRX8888:
				return width * height * 4;
			case VtfFormat.RGB888:
			case VtfFormat.BGR888:
				return width * height * 3;
			case VtfFormat.DXT1:
			{
				int blocksX = (width + 3) / 4;
				int blocksY = (height + 3) / 4;
				return blocksX * blocksY * 8;
			}
			case VtfFormat.DXT3:
			case VtfFormat.DXT5:
			{
				int blocksX = (width + 3) / 4;
				int blocksY = (height + 3) / 4;
				return blocksX * blocksY * 16;
			}
			default:
				return 0;
		}
	}

	private static byte[] DecompressDxt( IO.BinaryReader br, int width, int height, int blockBytes )
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];
		for ( int by = 0; by < blocksY; by++ )
		{
			for ( int bx = 0; bx < blocksX; bx++ )
			{
				var block = br.ReadBytes( blockBytes );
				DecodeDxtBlock( block, blockBytes, outRgba, width, bx * 4, by * 4 );
			}
		}
		return outRgba;
	}

	private static void DecodeDxtBlock( byte[] block, int blockBytes, byte[] dst, int width, int x, int y )
	{
		if ( blockBytes == 8 )
		{
			ushort c0 = (ushort)(block[1] << 8 | block[0]);
			ushort c1 = (ushort)(block[3] << 8 | block[2]);
			var colors = new (byte r, byte g, byte b, byte a)[4];
			colors[0] = Convert565( c0 );
			colors[1] = Convert565( c1 );
			if ( c0 > c1 )
			{
				colors[2] = Lerp( colors[0], colors[1], 2, 3 );
				colors[3] = Lerp( colors[0], colors[1], 1, 3 );
			}
			else
			{
				colors[2] = new(colors[0].r, colors[0].g, colors[0].b, 255);
				colors[3] = new(colors[1].r, colors[1].g, colors[1].b, 255);
			}
			uint indices = BitConverter.ToUInt32( block, 4 );
			WriteBlock( dst, width, x, y, colors, indices );
		}
		else
		{
			int alphaBase = 0;
			int colorBase = 8;
			byte a0 = block[alphaBase + 0];
			byte a1 = block[alphaBase + 1];
			ushort c0 = (ushort)(block[colorBase + 1] << 8 | block[colorBase + 0]);
			ushort c1 = (ushort)(block[colorBase + 3] << 8 | block[colorBase + 2]);
			var colors = new (byte r, byte g, byte b, byte a)[4];
			colors[0] = Convert565( c0 );
			colors[1] = Convert565( c1 );
			colors[2] = Lerp( colors[0], colors[1], 2, 3 );
			colors[3] = Lerp( colors[0], colors[1], 1, 3 );
			uint indices = BitConverter.ToUInt32( block, colorBase + 4 );
			WriteBlockWithAlpha( dst, width, x, y, colors, indices, a0, a1 );
		}
	}

	private static (byte r, byte g, byte b, byte a) Convert565( ushort c )
	{
		byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
		byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
		byte b = (byte)((c & 0x1F) * 255 / 31);
		return (r, g, b, 255);
	}

	private static (byte r, byte g, byte b, byte a) Lerp( (byte r, byte g, byte b, byte a) a, (byte r, byte g, byte b, byte a) b, int na, int d )
	{
		return (
			(byte)((a.r * na + b.r * (d - na)) / d),
			(byte)((a.g * na + b.g * (d - na)) / d),
			(byte)((a.b * na + b.b * (d - na)) / d),
			255);
	}

	private static void WriteBlock( byte[] dst, int width, int x, int y, (byte r, byte g, byte b, byte a)[] colors, uint indices )
	{
		int heightPx = Math.Max( 1, dst.Length / (4 * Math.Max(1,width)) );
		for ( int row = 0; row < 4; row++ )
		{
			for ( int col = 0; col < 4; col++ )
			{
				int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
				int px = x + col;
				int py = y + row;
				if ( px >= width || py >= heightPx ) continue;
				int di = (py * width + px) * 4;
				dst[di + 0] = colors[idx].r;
				dst[di + 1] = colors[idx].g;
				dst[di + 2] = colors[idx].b;
				dst[di + 3] = 255;
			}
		}
	}

	private static void WriteBlockWithAlpha( byte[] dst, int width, int x, int y, (byte r, byte g, byte b, byte a)[] colors, uint indices, byte a0, byte a1 )
	{
		int heightPx = Math.Max( 1, dst.Length / (4 * Math.Max(1,width)) );
		for ( int row = 0; row < 4; row++ )
		{
			for ( int col = 0; col < 4; col++ )
			{
				int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
				int px = x + col;
				int py = y + row;
				if ( px >= width || py >= heightPx ) continue;
				int di = (py * width + px) * 4;
				dst[di + 0] = colors[idx].r;
				dst[di + 1] = colors[idx].g;
				dst[di + 2] = colors[idx].b;
				dst[di + 3] = (byte)((a0 + a1) / 2);
			}
		}
	}

	private static byte[] DecompressDxt1( IO.BinaryReader br, int width, int height )
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];
		for ( int by = 0; by < blocksY; by++ )
		{
			for ( int bx = 0; bx < blocksX; bx++ )
			{
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint indices = br.ReadUInt32();
				var colors = new (byte r, byte g, byte b, byte a)[4];
				colors[0] = Convert565( c0 ); colors[0].a = 255;
				colors[1] = Convert565( c1 ); colors[1].a = 255;
				if ( c0 > c1 )
				{
					colors[2] = Lerp( colors[0], colors[1], 2, 3 );
					colors[3] = Lerp( colors[0], colors[1], 1, 3 );
				}
				else
				{
					colors[2] = ((byte)((colors[0].r + colors[1].r) / 2), (byte)((colors[0].g + colors[1].g) / 2), (byte)((colors[0].b + colors[1].b) / 2), (byte)255);
					colors[3] = (0, 0, 0, (byte)0);
				}
				WriteBlock( outRgba, width, bx * 4, by * 4, colors, indices );
			}
		}
		return outRgba;
	}

	private static byte[] DecompressDxt3( IO.BinaryReader br, int width, int height )
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];
		for ( int by = 0; by < blocksY; by++ )
		{
			for ( int bx = 0; bx < blocksX; bx++ )
			{
				ulong alphaBits = br.ReadUInt64();
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint indices = br.ReadUInt32();
				var colors = new (byte r, byte g, byte b, byte a)[4];
				colors[0] = Convert565( c0 );
				colors[1] = Convert565( c1 );
				colors[2] = Lerp( colors[0], colors[1], 2, 3 );
				colors[3] = Lerp( colors[0], colors[1], 1, 3 );
				WriteBlockWithExplicitAlpha( outRgba, width, bx * 4, by * 4, colors, indices, alphaBits );
			}
		}
		return outRgba;
	}

	private static byte[] DecompressDxt5( IO.BinaryReader br, int width, int height )
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];
		for ( int by = 0; by < blocksY; by++ )
		{
			for ( int bx = 0; bx < blocksX; bx++ )
			{
				byte a0 = br.ReadByte();
				byte a1 = br.ReadByte();
				ulong aIdx = br.ReadUInt16();
				aIdx |= (ulong)br.ReadUInt16() << 16;
				aIdx |= (ulong)br.ReadUInt16() << 32;
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint indices = br.ReadUInt32();

				var alphaVals = new byte[8];
				alphaVals[0] = a0; alphaVals[1] = a1;
				if ( a0 > a1 )
				{
					alphaVals[2] = (byte)((6 * a0 + 1 * a1) / 7);
					alphaVals[3] = (byte)((5 * a0 + 2 * a1) / 7);
					alphaVals[4] = (byte)((4 * a0 + 3 * a1) / 7);
					alphaVals[5] = (byte)((3 * a0 + 4 * a1) / 7);
					alphaVals[6] = (byte)((2 * a0 + 5 * a1) / 7);
					alphaVals[7] = (byte)((1 * a0 + 6 * a1) / 7);
				}
				else
				{
					alphaVals[2] = (byte)((4 * a0 + 1 * a1) / 5);
					alphaVals[3] = (byte)((3 * a0 + 2 * a1) / 5);
					alphaVals[4] = (byte)((2 * a0 + 3 * a1) / 5);
					alphaVals[5] = (byte)((1 * a0 + 4 * a1) / 5);
					alphaVals[6] = 0;
					alphaVals[7] = 255;
				}

				var colors = new (byte r, byte g, byte b, byte a)[4];
				colors[0] = Convert565( c0 );
				colors[1] = Convert565( c1 );
				colors[2] = Lerp( colors[0], colors[1], 2, 3 );
				colors[3] = Lerp( colors[0], colors[1], 1, 3 );
				WriteBlockWithIndexedAlpha( outRgba, width, bx * 4, by * 4, colors, indices, alphaVals, aIdx );
			}
		}
		return outRgba;
	}

	private static void WriteBlockWithExplicitAlpha( byte[] dst, int width, int x, int y, (byte r, byte g, byte b, byte a)[] colors, uint indices, ulong alphaBits )
	{
		int heightPx = Math.Max( 1, dst.Length / (4 * Math.Max(1,width)) );
		for ( int row = 0; row < 4; row++ )
		{
			for ( int col = 0; col < 4; col++ )
			{
				int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
				int px = x + col;
				int py = y + row;
				if ( px >= width || py >= heightPx ) continue;
				int di = (py * width + px) * 4;
				byte a4 = (byte)((alphaBits >> (4 * (row * 4 + col))) & 0xF);
				byte a = (byte)(a4 * 17);
				dst[di + 0] = colors[idx].r;
				dst[di + 1] = colors[idx].g;
				dst[di + 2] = colors[idx].b;
				dst[di + 3] = a;
			}
		}
	}

	private static void WriteBlockWithIndexedAlpha( byte[] dst, int width, int x, int y, (byte r, byte g, byte b, byte a)[] colors, uint indices, byte[] alphaVals, ulong aIdx )
	{
		int heightPx = Math.Max( 1, dst.Length / (4 * Math.Max(1,width)) );
		for ( int row = 0; row < 4; row++ )
		{
			for ( int col = 0; col < 4; col++ )
			{
				int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
				int px = x + col;
				int py = y + row;
				if ( px >= width || py >= heightPx ) continue;
				int di = (py * width + px) * 4;
				int aIndex = (int)((aIdx >> (3 * (row * 4 + col))) & 0x7);
				byte a = alphaVals[aIndex];
				dst[di + 0] = colors[idx].r;
				dst[di + 1] = colors[idx].g;
				dst[di + 2] = colors[idx].b;
				dst[di + 3] = a;
			}
		}
	}

	private enum VtfFormat
	{
		RGBA8888 = 0,
		ABGR8888 = 1,
		RGB888 = 2,
		BGR888 = 3,
		RGB565 = 4,
		I8 = 5,
		IA88 = 6,
		P8 = 7,
		A8 = 8,
		RGB888_BLUESCREEN = 9,
		BGR888_BLUESCREEN = 10,
		ARGB8888 = 11,
		BGRA8888 = 12,
		DXT1 = 13,
		DXT3 = 14,
		DXT5 = 15,
		BGRX8888 = 16,
		BGR565 = 17,
		BGRX5551 = 18,
		BGRA4444 = 19,
		DXT1_ONEBITALPHA = 20,
		BGRA5551 = 21,
		UV88 = 22,
		UVWQ8888 = 23,
		RGBA16161616F = 24,
		RGBA16161616 = 25,
		UVLX8888 = 26
	}
}



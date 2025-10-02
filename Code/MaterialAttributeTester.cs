using Sandbox;

/// <summary>
/// Test component to verify material attribute setting on GMod models
/// Similar to the user's example for testing combos and parameters
/// </summary>
public sealed class MaterialAttributeTester : Component
{
	[Property] public ModelRenderer Renderer { get; set; }
	[Property] public bool TestColorTint { get; set; } = true;
	[Property] public bool TestPhongCombo { get; set; } = true;
	[Property] public Vector3 DebugColorTint { get; set; } = new Vector3(0f, 2f, 2f); // Bright cyan

	protected override void OnAwake()
	{
		if (Renderer == null)
		{
			Log.Warning("[MaterialAttributeTester] No ModelRenderer assigned!");
			return;
		}

		Log.Info("[MaterialAttributeTester] Testing material attribute setting...");

		// Test setting attributes on material overrides (like user's example)
		for (int i = 0; i < 5; i++)
		{
			try
			{
				var mat = Renderer.Materials.GetOverride(i);
				if (mat != null)
				{
					Log.Info($"[MaterialAttributeTester] Testing material slot {i}");

					if (TestColorTint)
					{
						// Test color tinting
						SetVec3Param(mat, "g_vColorTint", DebugColorTint.x, DebugColorTint.y, DebugColorTint.z);
						Log.Info($"[MaterialAttributeTester] Set color tint to {DebugColorTint}");
					}

					if (TestPhongCombo)
					{
						// Test phong combo setting
						if (mat.Attributes != null)
						{
							mat.Attributes.SetCombo("S_PHONG_EXPONENT_TEXTURE", 1);
							Log.Info("[MaterialAttributeTester] Set S_PHONG_EXPONENT_TEXTURE combo to 1");
						}
						else
						{
							Log.Warning("[MaterialAttributeTester] Material.Attributes is null!");
						}
					}
				}
				else
				{
					Log.Info($"[MaterialAttributeTester] Material slot {i} is null");
				}
			}
			catch (System.Exception ex)
			{
				Log.Warning($"[MaterialAttributeTester] Error accessing material slot {i}: {ex.Message}");
			}
		}
	}

	private static void SetVec3Param(Material mat, string name, float x, float y, float z)
	{
		try
		{
			mat.Set(name, new System.Numerics.Vector3(x, y, z));
			Log.Info($"[MaterialAttributeTester] Set vec3 param {name} = ({x}, {y}, {z})");
		}
		catch (System.Exception ex)
		{
			Log.Warning($"[MaterialAttributeTester] Failed to set vec3 param {name}: {ex.Message}");
		}
	}
}
































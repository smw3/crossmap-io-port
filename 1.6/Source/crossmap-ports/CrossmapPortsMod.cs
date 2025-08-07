using Verse;
using HarmonyLib;
using System;
using UnityEngine;

namespace CrossmapPorts
{
	internal class CrossmapPortsMod : Mod
	{
		public CrossmapPortsMod(ModContentPack content) : base(content)
		{
			new Harmony("CrossmapPorts").PatchAll();
		}
	}
}

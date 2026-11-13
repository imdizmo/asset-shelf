using UnrealBuildTool;

public class AssetShelf : ModuleRules
{
	public AssetShelf(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
		PrivateDependencyModuleNames.AddRange(new[] { "UnrealEd", "AssetRegistry", "Json", "ImageCore", "RenderCore", "Niagara" });
	}
}

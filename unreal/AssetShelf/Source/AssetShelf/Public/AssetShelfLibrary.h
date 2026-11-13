#pragma once

#include "Kismet/BlueprintFunctionLibrary.h"
#include "AssetShelfLibrary.generated.h"

UCLASS()
class UAssetShelfLibrary : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()
public:
	/** Writes <Out>/index.json and <Out>/thumbs/ pictures for every asset under Roots (/Game/...). Size 0 = no pictures.
	 *  bEmbeddedOnly: take only the pictures saved inside the asset files (the Content Browser's), never load or render
	 *  an asset; no GPU needed. */
	UFUNCTION(BlueprintCallable, Category = "Asset Shelf")
	static bool IndexPaths(const TArray<FString>& Roots, const FString& Out, int32 Size = 256, bool bEmbeddedOnly = false);
};

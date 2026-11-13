#pragma once

#include "Commandlets/Commandlet.h"
#include "AssetShelfIndexCommandlet.generated.h"

/**
 * Writes an index of every asset under the given content roots: class, registry tags, hard and soft
 * dependencies, referencers, and a preview picture.
 *   UnrealEditor-Cmd Host.uproject -run=AssetShelfIndex -Roots=/Game/A+/Game/B -Out=/abs/dir [-Size=256]
 *   -AllowCommandletRendering -unattended
 * Output: <Out>/index.json and <Out>/thumbs/<hash>.png
 */
UCLASS()
class UAssetShelfIndexCommandlet : public UCommandlet
{
	GENERATED_BODY()
public:
	UAssetShelfIndexCommandlet();
	virtual int32 Main(const FString& Params) override;
};

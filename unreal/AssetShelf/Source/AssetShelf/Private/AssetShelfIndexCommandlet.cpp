#include "AssetShelfIndexCommandlet.h"
#include "AssetShelfLibrary.h"

#include "AssetCompilingManager.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "HAL/FileManager.h"
#include "ImageCore.h"
#include "ImageUtils.h"
#include "Misc/FileHelper.h"
#include "Misc/ObjectThumbnail.h"
#include "Misc/Paths.h"
#include "Misc/SecureHash.h"
#include "ObjectTools.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonWriter.h"
#include "ShaderCompiler.h"
#include "UObject/UObjectGlobals.h"

DEFINE_LOG_CATEGORY_STATIC(LogAssetShelf, Display, All);

using FJsonOut = TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>;

UAssetShelfIndexCommandlet::UAssetShelfIndexCommandlet()
{
	IsClient = false;
	IsEditor = true;
	IsServer = false;
	LogToConsole = true;
}

static bool NoPicture(const FString& Class)
{
	// ponytail: name-based skip list; a level gets its picture from a separate top-down pass later
	static const TCHAR* Skip[] = { TEXT("World"), TEXT("Sound"), TEXT("Dialogue"), TEXT("Reverb"), TEXT("Attenuation"),
		TEXT("Concurrency"), TEXT("DataTable"), TEXT("CurveTable"), TEXT("Redirector"), TEXT("BuildData"), TEXT("HLOD") };
	for (const TCHAR* S : Skip)
	{
		if (Class.Contains(S)) return true;
	}
	return false;
}

static void WriteNames(FJsonOut& W, const TCHAR* Key, const TArray<FName>& Names)
{
	W.WriteArrayStart(Key);
	for (const FName& N : Names)
	{
		const FString S = N.ToString();
		if (!S.StartsWith(TEXT("/Script/"))) W.WriteValue(S);
	}
	W.WriteArrayEnd();
}

bool AssetShelfFxPicture(UObject* Asset, int32 Size, const FString& File);

static bool SaveImage(TArray<uint8>& Px, int32 W, int32 H, const FString& File)
{
	// sizes come from third-party package files: bound them and check in 64 bits so W * H * 4 cannot wrap
	if (W <= 0 || H <= 0 || W > 4096 || H > 4096 || (int64)W * H * 4 > Px.Num()) return false;
	for (int32 i = 3; i < Px.Num(); i += 4) Px[i] = 255;
	const FImageView View(Px.GetData(), W, H, ERawImageFormat::BGRA8);
	return FImageUtils::SaveImageByExtension(*File, View);
}

/** The picture saved inside the asset's package by the Content Browser, if it has one; reads the file, loads nothing. */
static bool SaveEmbedded(const FAssetData& A, const FString& File)
{
	FObjectThumbnail Thumb;
	if (!ThumbnailTools::LoadThumbnailFromPackage(A, Thumb) || Thumb.IsEmpty()) return false;
	TArray<uint8> Px = Thumb.GetUncompressedImageData();
	return SaveImage(Px, Thumb.GetImageWidth(), Thumb.GetImageHeight(), File);
}

static bool SavePicture(UObject* Obj, int32 Size, const FString& File)
{
	FAssetCompilingManager::Get().FinishAllCompilation();
	if (GShaderCompilingManager) GShaderCompilingManager->FinishAllCompilation();
	if (Obj->GetClass()->GetName() == TEXT("NiagaraSystem") || Obj->GetClass()->GetName() == TEXT("ParticleSystem"))
	{
		// ASSET_SHELF_NO_FX=1: no effect pictures (playing effects off-screen can starve the desktop's GPU)
		if (!FPlatformMisc::GetEnvironmentVariable(TEXT("ASSET_SHELF_NO_FX")).IsEmpty()) return false;
		return AssetShelfFxPicture(Obj, Size, File);
	}
	FObjectThumbnail Thumb;
	ThumbnailTools::RenderThumbnail(Obj, Size, Size, ThumbnailTools::EThumbnailTextureFlushMode::AlwaysFlush, nullptr, &Thumb);
	return SaveImage(Thumb.AccessImageData(), Thumb.GetImageWidth(), Thumb.GetImageHeight(), File);
}

bool UAssetShelfLibrary::IndexPaths(const TArray<FString>& Roots, const FString& Out, int32 Size, bool bEmbeddedOnly)
{
	const bool bPictures = Size > 0;
	const FString RootsArg = FString::Join(Roots, TEXT("+"));
	IFileManager::Get().MakeDirectory(*(Out / TEXT("thumbs")), true);

	IAssetRegistry& AR = FModuleManager::LoadModuleChecked<FAssetRegistryModule>("AssetRegistry").Get();
	AR.ScanPathsSynchronous(Roots, true);
	AR.WaitForCompletion();

	FARFilter Filter;
	for (const FString& R : Roots) Filter.PackagePaths.Add(*R);
	Filter.bRecursivePaths = true;
	TArray<FAssetData> Assets;
	AR.GetAssets(Filter, Assets);
	Assets.RemoveAll([](const FAssetData& A) { return A.PackagePath.ToString().Contains(TEXT("/__External")); });
	Assets.Sort([](const FAssetData& A, const FAssetData& B) { return A.PackageName.LexicalLess(B.PackageName); });
	UE_LOG(LogAssetShelf, Display, TEXT("%d assets under %s"), Assets.Num(), *RootsArg);

	FString Json;
	TSharedRef<FJsonOut> W = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Json);
	W->WriteObjectStart();
	W->WriteValue(TEXT("engine"), FEngineVersion::Current().ToString(EVersionComponent::Patch));
	W->WriteArrayStart(TEXT("assets"));
	int32 Pictures = 0;
	const double T0 = FPlatformTime::Seconds();
	for (int32 i = 0; i < Assets.Num(); ++i)
	{
		const FAssetData& A = Assets[i];
		const FString Path = A.GetSoftObjectPath().ToString();
		const FString Class = A.AssetClassPath.GetAssetName().ToString();
		W->WriteObjectStart();
		W->WriteValue(TEXT("path"), Path);
		W->WriteValue(TEXT("package"), A.PackageName.ToString());
		W->WriteValue(TEXT("name"), A.AssetName.ToString());
		W->WriteValue(TEXT("class"), Class);

		W->WriteObjectStart(TEXT("tags"));
		for (const auto& Tag : A.TagsAndValues)
		{
			const FString V = Tag.Value.AsString();
			if (V.Len() <= 200) W->WriteValue(Tag.Key.ToString(), V);
		}
		W->WriteObjectEnd();

		TArray<FName> Names;
		AR.GetDependencies(A.PackageName, Names, UE::AssetRegistry::EDependencyCategory::Package, UE::AssetRegistry::EDependencyQuery::Hard);
		WriteNames(*W, TEXT("deps"), Names);
		Names.Reset();
		AR.GetDependencies(A.PackageName, Names, UE::AssetRegistry::EDependencyCategory::Package, UE::AssetRegistry::EDependencyQuery::Soft);
		WriteNames(*W, TEXT("soft"), Names);
		Names.Reset();
		AR.GetReferencers(A.PackageName, Names, UE::AssetRegistry::EDependencyCategory::Package, UE::AssetRegistry::EDependencyQuery::Hard);
		WriteNames(*W, TEXT("refs"), Names);

		if (bPictures && !NoPicture(Class))
		{
			const FString Name = FMD5::HashAnsiString(*Path) + TEXT(".png");
			if (bEmbeddedOnly)
			{
				// the saved picture wins; without one, a picture an earlier rendering pass left there is kept
				const FString File = Out / TEXT("thumbs") / Name;
				const bool bSaved = SaveEmbedded(A, File);
				if (bSaved || IFileManager::Get().FileExists(*File))
				{
					W->WriteValue(TEXT("thumb"), TEXT("thumbs/") + Name);
					W->WriteValue(TEXT("thumbsrc"), bSaved ? TEXT("embedded") : TEXT("rendered"));
					++Pictures;
				}
			}
			else if (UObject* Obj = A.GetAsset())
			{
				if (SavePicture(Obj, Size, Out / TEXT("thumbs") / Name))
				{
					W->WriteValue(TEXT("thumb"), TEXT("thumbs/") + Name);
					++Pictures;
				}
			}
		}
		W->WriteObjectEnd();

		if (i % 40 == 39)
		{
			CollectGarbage(RF_NoFlags);
			UE_LOG(LogAssetShelf, Display, TEXT("%d/%d (%d pictures, %.0fs)"), i + 1, Assets.Num(), Pictures, FPlatformTime::Seconds() - T0);
		}
	}
	W->WriteArrayEnd();
	W->WriteObjectEnd();
	W->Close();
	FFileHelper::SaveStringToFile(Json, *(Out / TEXT("index.json")), FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
	UE_LOG(LogAssetShelf, Display, TEXT("ASSETSHELF DONE %d assets, %d pictures, %.0fs"), Assets.Num(), Pictures, FPlatformTime::Seconds() - T0);
	return true;
}

int32 UAssetShelfIndexCommandlet::Main(const FString& Params)
{
	FString RootsArg, Out;
	int32 Size = 256;
	FParse::Value(*Params, TEXT("Roots="), RootsArg);
	FParse::Value(*Params, TEXT("Out="), Out);
	FParse::Value(*Params, TEXT("Size="), Size);
	const bool bPictures = !FParse::Param(*Params, TEXT("NoPictures"));
	const bool bEmbedded = FParse::Param(*Params, TEXT("Embedded"));
	TArray<FString> Roots;
	RootsArg.ParseIntoArray(Roots, TEXT("+"));
	if (Roots.IsEmpty() || Out.IsEmpty())
	{
		UE_LOG(LogAssetShelf, Error, TEXT("usage: -run=AssetShelfIndex -Roots=/Game/A+/Game/B -Out=<dir> [-Size=256] [-NoPictures] [-Embedded]"));
		return 1;
	}
	return UAssetShelfLibrary::IndexPaths(Roots, Out, bPictures ? Size : 0, bEmbedded) ? 0 : 1;
}

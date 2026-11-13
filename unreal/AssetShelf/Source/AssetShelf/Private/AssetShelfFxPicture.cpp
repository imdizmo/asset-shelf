// Pictures of particle effects: Unreal's own thumbnail for them is only a saved image, so play the effect in a
// small preview scene, in front of a grey backdrop, and keep the frame that differs most from the backdrop.
// Effect bounds are often a fixed default box, so a first pass finds the effect on screen and a second pass frames it.

#include "Components/SceneCaptureComponent2D.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/TextureRenderTarget2D.h"
#include "ImageCore.h"
#include "ImageUtils.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "NiagaraComponent.h"
#include "NiagaraSystem.h"
#include "Particles/ParticleSystem.h"
#include "Particles/ParticleSystemComponent.h"
#include "PreviewScene.h"
#include "TextureResource.h"

namespace
{
struct FShot
{
	TArray<FColor> Px;
	double Score = -1;
	FIntRect Box;  // pixels that differ from the backdrop
};

int32 Diff(const FColor& A, const FColor& B)
{
	return FMath::Abs(A.R - B.R) + FMath::Abs(A.G - B.G) + FMath::Abs(A.B - B.B);
}
}

bool AssetShelfFxPicture(UObject* Asset, int32 Size, const FString& File)
{
	UFXSystemComponent* Fx = nullptr;
	if (UNiagaraSystem* S = Cast<UNiagaraSystem>(Asset))
	{
		S->WaitForCompilationComplete();
		UNiagaraComponent* N = NewObject<UNiagaraComponent>(GetTransientPackage());
		N->SetAutoActivate(false);
		N->SetAsset(S);
		Fx = N;
	}
	else if (UParticleSystem* P = Cast<UParticleSystem>(Asset))
	{
		UParticleSystemComponent* C = NewObject<UParticleSystemComponent>(GetTransientPackage());
		C->bAutoActivate = false;
		C->SetTemplate(P);
		Fx = C;
	}
	if (!Fx) return false;

	FPreviewScene Scene(FPreviewScene::ConstructionValues().SetCreatePhysicsScene(false));
	UWorld* World = Scene.GetWorld();
	Scene.AddComponent(Fx, FTransform::Identity);

	const int32 Res = Size * 2;
	UTextureRenderTarget2D* Target = NewObject<UTextureRenderTarget2D>(GetTransientPackage());
	Target->InitCustomFormat(Res, Res, PF_B8G8R8A8, false);
	Target->UpdateResourceImmediate(true);
	USceneCaptureComponent2D* Cap = NewObject<USceneCaptureComponent2D>(GetTransientPackage());
	Cap->TextureTarget = Target;
	Cap->CaptureSource = SCS_FinalColorLDR;
	Cap->bCaptureEveryFrame = false;
	Cap->bCaptureOnMovement = false;
	Cap->FOVAngle = 40.f;
	Cap->ShowFlags.SetEyeAdaptation(false);  // auto exposure starts dark and has no time to adapt
	Cap->PostProcessBlendWeight = 1.f;
	Cap->PostProcessSettings.bOverride_AutoExposureBias = true;
	Cap->PostProcessSettings.AutoExposureBias = -2.f;  // effects are made for dark game scenes; unbiased they burn to white
	Cap->PostProcessSettings.bOverride_BloomIntensity = true;
	Cap->PostProcessSettings.BloomIntensity = 0.3f;
	Scene.AddComponent(Cap, FTransform::Identity);

	// backdrop: a big plane behind the effect, lit grey, so dark smoke shows as well as bright fire
	UStaticMeshComponent* Back = NewObject<UStaticMeshComponent>(GetTransientPackage());
	Back->SetStaticMesh(LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Plane.Plane")));
	if (UMaterialInterface* M = LoadObject<UMaterialInterface>(nullptr, TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial")))
	{
		UMaterialInstanceDynamic* Mi = UMaterialInstanceDynamic::Create(M, GetTransientPackage());
		Mi->SetVectorParameterValue(TEXT("Color"), FLinearColor(0.05f, 0.05f, 0.055f));
		Back->SetMaterial(0, Mi);
	}
	Back->CastShadow = false;
	Scene.AddComponent(Back, FTransform::Identity);

	const FVector Look = FVector(1.f, 0.35f, -0.3f).GetSafeNormal();  // from the front, a little above and aside
	const float HalfFov = FMath::DegreesToRadians(Cap->FOVAngle / 2);

	auto Run = [&](const FVector& Center, float Dist) -> FShot
	{
		Fx->DeactivateImmediate();
		Fx->Activate(true);
		Cap->SetWorldLocationAndRotation(Center - Look * Dist, Look.Rotation());
		// the plane faces +Z: turn it to face the camera, twice the effect's distance behind it
		Back->SetWorldTransform(FTransform(FRotationMatrix::MakeFromZ(-Look).Rotator(), Center + Look * Dist * 2, FVector(Dist / 10)));
		FShot Best;
		TArray<FColor> Px;
		const float Dt = 1.f / 30.f;
		for (int32 Frame = 1; Frame <= 90; ++Frame)
		{
			World->Tick(LEVELTICK_All, Dt);
			if (UNiagaraComponent* N = Cast<UNiagaraComponent>(Fx))
			{
				N->AdvanceSimulation(1, Dt);  // preview worlds do not tick Niagara on their own
			}
			else
			{
				Fx->TickComponent(Dt, LEVELTICK_All, nullptr);
			}
			Cap->CaptureScene();  // every frame: GPU particles only move when the scene renders
			if (Frame % 10 != 0) continue;  // judge at 1/3 s steps
			Px.Reset();
			Target->GameThread_GetRenderTargetResource()->ReadPixels(Px);
			if (Px.Num() != Res * Res) continue;
			const FColor Bg = Px[0];
			double Score = 0;
			FIntRect Box(Res, Res, -1, -1);
			for (int32 Y = 0; Y < Res; ++Y)
			{
				for (int32 X = 0; X < Res; ++X)
				{
					const int32 D = Diff(Px[Y * Res + X], Bg);
					Score += D;
					if (D > 30)
					{
						Box.Min.X = FMath::Min(Box.Min.X, X);
						Box.Min.Y = FMath::Min(Box.Min.Y, Y);
						Box.Max.X = FMath::Max(Box.Max.X, X);
						Box.Max.Y = FMath::Max(Box.Max.Y, Y);
					}
				}
			}
			if (Score > Best.Score)
			{
				Best.Score = Score;
				Best.Px = Px;
				Best.Box = Box;
			}
		}
		return Best;
	};

	const FBoxSphereBounds B = Fx->Bounds;
	FVector Center = B.Origin;
	float Dist = FMath::Clamp(B.SphereRadius, 50.f, 3000.f) / FMath::Tan(HalfFov) * 1.1f;
	FShot Shot = Run(Center, Dist);
	if (Shot.Box.Max.X > Shot.Box.Min.X + 2)
	{
		// move the camera so the changed pixels fill about 80% of the frame
		const float ViewW = 2 * Dist * FMath::Tan(HalfFov);
		const FRotationMatrix Rot(Look.Rotation());
		const FVector Right = Rot.GetUnitAxis(EAxis::Y);
		const FVector Up = Rot.GetUnitAxis(EAxis::Z);
		const double MidX = (Shot.Box.Min.X + Shot.Box.Max.X) / 2.0 / Res - 0.5;
		const double MidY = (Shot.Box.Min.Y + Shot.Box.Max.Y) / 2.0 / Res - 0.5;
		Center += Right * (MidX * ViewW) - Up * (MidY * ViewW);
		const float Span = FMath::Max(Shot.Box.Width(), Shot.Box.Height()) / float(Res);
		Dist *= FMath::Clamp(Span / 0.8f, 0.02f, 1.5f);
		FShot Closer = Run(Center, Dist);
		if (Closer.Score > 0) Shot = MoveTemp(Closer);
	}
	UE_LOG(LogTemp, Display, TEXT("ASSETSHELF FX %s dist %.0f score %.0f"), *Asset->GetName(), Dist, Shot.Score);
	Fx->DeactivateImmediate();
	if (Shot.Px.Num() != Res * Res) return false;
	for (FColor& C : Shot.Px) C.A = 255;
	FImage Small;
	FImageCore::ResizeImageAllocDest(FImageView(Shot.Px.GetData(), Res, Res), Small, Size, Size);
	return FImageUtils::SaveImageByExtension(*File, Small);
}

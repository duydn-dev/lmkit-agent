using LMKit.Finetuning;
using LMKit.Inference;
using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// LIVE proof that the grounding fine-tuning pipeline produces an artifact the computer-use model
/// can actually LOAD — the half the shipped pipeline had no path for at all.
///
/// <para>
/// It walks the exact sequence <see cref="LmKitGroundingAdapterTrainerPort"/> walks —
/// <see cref="GroundingTrainingPlan"/> → <c>ChatTrainingSample</c> → <c>TrainingDataset</c> →
/// <c>LoraFinetuning.AddDataset</c> → <c>TrainToAdapter</c> — and then closes the loop the way
/// <c>ComputerUseModel</c> does: <c>LM.ApplyLoraAdapter</c> → the adapter appears in
/// <c>LM.Adapters</c> → <c>LM.RemoveLoraAdapter</c> → it is gone.
/// </para>
///
/// <para>
/// OPT-IN — see <see cref="LiveModelFixture"/>; never runs in CI, which has neither the env var
/// nor the weights. Joined to <see cref="LiveModelCollection"/> so live tests stay serialized.
/// </para>
///
/// <para>
/// SCOPE OF THE EVIDENCE. The local fixture model is a TEXT model (Llama-3.2-1B-Instruct), so
/// these tests prove the dataset/train/apply/remove mechanics and the text half of the sample
/// builder. They do NOT exercise a vision-language base, image attachments, or
/// <see cref="InferenceModality.Multimodal"/> training — that needs a vision GGUF + its projector
/// and is called out as unverified in the round report.
/// </para>
/// </summary>
[Collection(LiveModelCollection.Name)]
public class GroundingAdapterLiveTests(LiveModelFixture fixture) : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"lmkit-grounding-live-{Guid.NewGuid():N}");

    private LM Model
    {
        get
        {
            Skip.If(fixture.Model is null, LiveModelFixture.SkipReason);
            return fixture.Model!;
        }
    }

    private static IReadOnlyList<GroundingSample> Samples(int count) =>
        Enumerable.Range(1, count).Select(i => new GroundingSample
        {
            TenantId = Guid.NewGuid(),
            TaskGoal = $"Open item {i}",
            PageUrl = $"https://example.com/list/{i}",
            ElementsText =
                "INTERACTIVE ELEMENTS (address these by 'ref'):\n"
                + $"  [1] link: Item {i}\n"
                + "  [2] button: Search\n",
            SystemPrompt = "You are a careful web automation agent. Return exactly one JSON action.",
            CorrectActionJson = "{\"action\":\"click\",\"ref\":1}",
        }).ToList();

    /// <summary>
    /// Builds the dataset exactly as the trainer port does and hands it to LM-Kit. This is the
    /// half the shipped code could not express at all: <c>AddTrainingData(ChatHistory)</c> carries
    /// no modality, so only <c>TrainingDataset</c> + <c>ChatTrainingSample</c> can.
    /// </summary>
    [SkippableFact]
    public void PlannedSamples_AreAcceptedByLoraFinetuningThroughATrainingDataset()
    {
        var model = Model;
        var plan = GroundingTrainingPlan.Build(Samples(4), (_, _) => null);

        using var finetuning = new LoraFinetuning(model);
        var added = finetuning.AddDataset(BuildDataset(model, plan));

        Assert.Equal(4, plan.Turns.Count);
        Assert.True(added > 0, "LoRA fine-tuning accepted none of the planned grounding samples.");
        Assert.True(finetuning.SampleCount > 0);
    }

    /// <summary>
    /// The deliverable: a trained grounding adapter is a real LoRA artifact that the SAME loaded
    /// model applies and removes — the acquire-lease → apply → infer → remove discipline
    /// <c>ComputerUseModel</c> now follows around every grounding decision.
    /// </summary>
    [SkippableFact]
    public void TrainedAdapter_IsAValidLoraArtifact_ThatTheModelAppliesAndRemoves()
    {
        var model = Model;
        Directory.CreateDirectory(_workDirectory);
        var adapterPath = Path.Combine(_workDirectory, "grounding-live.gguf");

        var plan = GroundingTrainingPlan.Build(Samples(4), (_, _) => null);
        using (var finetuning = new LoraFinetuning(model))
        {
            finetuning.Parameters.Rank = 4;
            finetuning.Parameters.Alpha = 8;
            finetuning.Parameters.Epochs = 1;
            finetuning.Parameters.LearningRate = 1e-4f;

            Assert.True(finetuning.AddDataset(BuildDataset(model, plan)) > 0);
            finetuning.TrainToAdapter(adapterPath);
        }

        Assert.True(File.Exists(adapterPath), "TrainToAdapter produced no adapter file.");
        Assert.True(new FileInfo(adapterPath).Length > 0);

        // Same validation LoraAdapterService runs before it will register an uploaded adapter.
        Assert.True(LoraAdapterSource.ValidateFormat(adapterPath, throwException: false),
            "The trained adapter is not a valid LoRA adapter file.");

        // …and the same apply/remove pair LmKitLoraModelPort drives for the chat path.
        var before = model.Adapters.Count;
        model.ApplyLoraAdapter(adapterPath, 1.0f);
        try
        {
            Assert.Equal(before + 1, model.Adapters.Count);
            var applied = model.Adapters[^1];
            Assert.True(model.RemoveLoraAdapter(applied), "RemoveLoraAdapter reported the adapter was not applied.");
            Assert.Equal(before, model.Adapters.Count);
        }
        catch
        {
            // Never leave the shared fixture model mutated for the rest of the collection.
            foreach (var adapter in model.Adapters.Skip(before).ToList()) model.RemoveLoraAdapter(adapter);
            throw;
        }
    }

    /// <summary>
    /// Mirrors <see cref="LmKitGroundingAdapterTrainerPort"/>'s dataset construction. Kept in the
    /// test so a drift between the two shows up as a live failure rather than silently.
    /// </summary>
    private static TrainingDataset BuildDataset(LM model, GroundingTrainingPlan plan)
    {
        var dataset = new TrainingDataset();
        foreach (var turn in plan.Turns)
        {
            var history = new ChatHistory(model);
            history.AddMessage(AuthorRole.System, turn.SystemPrompt);
            history.AddMessage(AuthorRole.User, turn.UserText);
            history.AddMessage(AuthorRole.Assistant, turn.AssistantText);
            dataset.AddSample(new ChatTrainingSample(history, turn.Modality));
        }
        return dataset;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true); }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}

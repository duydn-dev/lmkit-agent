using LmKitOmniApi.Application.LoraAdapters;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure, model-free tests for the LoRA adapter validation + DTO mapping shared by the
/// register/update handlers. Scale bounds are passed in (from LoraOptions) so the rules
/// stay options-free.
/// </summary>
public sealed class LoraAdapterRulesTests
{
    [Fact]
    public void Validate_AcceptsNullName_AsUnchanged()
    {
        // Update path passes null name/scale to mean "leave unchanged".
        Assert.Null(LoraAdapterRules.Validate(name: null, scale: null, minScale: 0f, maxScale: 2f));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyName(string name)
    {
        Assert.Equal("Tên adapter là bắt buộc.", LoraAdapterRules.Validate(name, null, 0f, 2f));
    }

    [Fact]
    public void Validate_RejectsOverlongName()
    {
        var error = LoraAdapterRules.Validate(new string('n', 81), null, 0f, 2f);
        Assert.Equal("Tên adapter không được vượt quá 80 ký tự.", error);
    }

    [Fact]
    public void Validate_AcceptsNameAtLimit()
    {
        Assert.Null(LoraAdapterRules.Validate(new string('n', 80), 1.0f, 0f, 2f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    public void Validate_AcceptsScaleWithinBounds(float scale)
    {
        Assert.Null(LoraAdapterRules.Validate("ok", scale, 0f, 2f));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(2.1f)]
    [InlineData(100f)]
    public void Validate_RejectsScaleOutOfBounds(float scale)
    {
        var error = LoraAdapterRules.Validate("ok", scale, 0f, 2f);
        Assert.NotNull(error);
        Assert.Contains("[0, 2]", error);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Validate_RejectsNonFiniteScale(float scale)
    {
        Assert.Equal("Hệ số scale không hợp lệ.", LoraAdapterRules.Validate("ok", scale, 0f, 2f));
    }

    [Fact]
    public void Validate_RejectsOverlongDescriptionAndTargetModelId()
    {
        Assert.Equal(
            "Mô tả không được vượt quá 300 ký tự.",
            LoraAdapterRules.Validate("ok", 1f, 0f, 2f, description: new string('d', 301)));
        Assert.Equal(
            "Model đích không được vượt quá 200 ký tự.",
            LoraAdapterRules.Validate("ok", 1f, 0f, 2f, targetModelId: new string('m', 201)));
    }

    [Fact]
    public void ToDto_MapsFields_AndNeverExposesFilePath()
    {
        var entity = new LoraAdapterRegistration
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "adapter",
            Description = "desc",
            FilePath = @"C:\secret\server\path\adapter.gguf",
            Scale = 1.25f,
            TargetModelId = "qwen3.5:2b",
            FileSizeBytes = 4096,
            IsActive = true
        };

        var dto = LoraAdapterRules.ToDto(entity);

        Assert.Equal(entity.Id, dto.Id);
        Assert.Equal("adapter", dto.Name);
        Assert.Equal("desc", dto.Description);
        Assert.Equal(1.25f, dto.Scale);
        Assert.Equal("qwen3.5:2b", dto.TargetModelId);
        Assert.Equal(4096, dto.FileSizeBytes);
        Assert.True(dto.IsActive);

        // The wire DTO must have no member carrying the server file path.
        Assert.DoesNotContain(
            typeof(LoraAdapterDto).GetProperties(),
            p => p.Name.Contains("File", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(string));
    }
}

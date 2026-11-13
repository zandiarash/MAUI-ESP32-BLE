using IotStarter.App.Core.Provisioning;
using IotStarter.App.Core.State;
using IotStarter.App.Resources;
using IotStarter.Shared;
using Microsoft.Extensions.Localization;

namespace IotStarter.App.Components;

/// <summary>Maps app states to localised, user-facing text (presentation only).</summary>
public static class UiText
{
    public static string ModelName(this IStringLocalizer<Strings> l, ushort modelId)
    {
        ModelDefinition? model = ModelCatalog.Find(modelId);
        if (model is null)
        {
            return l["Model.Unknown", ModelCatalog.FormatModelId(modelId)];
        }

        LocalizedString localized = l["Model." + model.IconKey];
        return localized.ResourceNotFound ? model.Name : localized.Value;
    }

    public static string Connectivity(this IStringLocalizer<Strings> l, DeviceConnectivity connectivity) => connectivity switch
    {
        DeviceConnectivity.Online => l["Status.Online"],
        DeviceConnectivity.Offline => l["Status.Offline"],
        DeviceConnectivity.Unauthorized => l["Status.Unauthorized"],
        _ => l["Status.Checking"]
    };

    public static string DotClass(DeviceConnectivity connectivity) => connectivity switch
    {
        DeviceConnectivity.Online => "status-dot status-dot--online",
        DeviceConnectivity.Unauthorized => "status-dot status-dot--warning",
        DeviceConnectivity.Offline => "status-dot",
        _ => "status-dot status-dot--checking"
    };

    public static string FailureTitle(this IStringLocalizer<Strings> l, FailureKind kind, string? ssid) =>
        l[$"Failure.{kind}.Title", ssid ?? string.Empty];

    public static string FailureAdvice(this IStringLocalizer<Strings> l, FailureKind kind) =>
        l[$"Failure.{kind}.Advice"];

    public static string Notice(this IStringLocalizer<Strings> l, NoticeKind kind, string? deviceName) =>
        l[$"Notice.{kind}", deviceName ?? string.Empty];

    public static string Result(this IStringLocalizer<Strings> l, DeviceActionResult result) => result switch
    {
        DeviceActionResult.Unreachable => l["Action.Unreachable"],
        DeviceActionResult.Unauthorized => l["Action.Unauthorized"],
        DeviceActionResult.Rejected => l["Action.Rejected"],
        _ => l["Action.Failed"]
    };

    public static string Credential(this IStringLocalizer<Strings> l, CredentialProblem problem) =>
        problem == CredentialProblem.None ? string.Empty : l[$"Credential.{problem}"];
}

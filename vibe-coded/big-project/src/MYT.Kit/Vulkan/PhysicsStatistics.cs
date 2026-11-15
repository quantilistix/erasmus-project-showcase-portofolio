namespace MYT.Kit.Vulkan;


/// <summary>What the last <see cref="PhysicsSystem.Update"/> did.</summary>
/// <param name="ContactCount">Contacts alive in the last substep.</param>
/// <param name="DroppedContacts">
/// Contacts, or candidate triangle pairs that could have made some, lost because
/// <see cref="PhysicsSettings.MaxContacts"/> was reached; raise it if this is not 0.
/// </param>
/// <param name="DroppedContactReferences">Internal list overflow; should always be 0.</param>
/// <param name="GpuMilliseconds">GPU time of the whole update, or 0 if the device cannot time compute work.</param>
/// <param name="DroppedContactReports">
/// Contacts left out of <see cref="PhysicsSystem.ContactReports"/> because
/// <see cref="PhysicsSettings.MaxContactReports"/> was reached.
/// </param>
public readonly record struct PhysicsStatistics
(
    uint ContactCount,
    uint DroppedContacts,
    uint DroppedContactReferences,
    double GpuMilliseconds,
    uint DroppedContactReports
)
{
    /// <summary>Candidate triangle pairs tested across detection passes in the last substep.</summary>
    public uint CandidatePairCount { get; init; }

    /// <summary>Contacts restored from stable feature identities in the previous substep.</summary>
    public uint WarmStartedContactCount { get; init; }

    /// <summary>Rigid bodies assigned to the ordered serial overflow queue in the last substep.</summary>
    public uint SerialRigidBodyCount { get; init; }

    /// <summary>Moving collision triangles in the scene; these are processed by each detection pass.</summary>
    public uint DynamicTriangleCount { get; init; }
}

/// <summary>
/// CPU wall times of the last update, in milliseconds. Wait includes GPU execution and
/// earlier queued work; it must not be added to GPU time as an independent cost.
/// </summary>
public readonly record struct PhysicsTimings
(
    double RebuildMilliseconds,
    double PreparationMilliseconds,
    double RecordingMilliseconds,
    double SubmissionMilliseconds,
    double WaitMilliseconds,
    double ReadbackMilliseconds,
    double TotalMilliseconds
);

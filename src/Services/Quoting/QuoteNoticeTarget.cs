namespace WorkshopDashboard.Services.Quoting;

/// <summary>Which quote flow a set of admin-configured notices applies to — one per repairer, split further
/// only where the repairer's own page has distinct flows: Dorado's
/// rigid/semi-rigid/specialty tabs price differently, so a flex-scope notice shouldn't show up on
/// a rigid Dorado quote. Falcon is surgical instruments only — no rigid/flex distinction — so it
/// gets one target like the rest.</summary>
public enum QuoteNoticeTarget
{
    DoradoRigid,
    DoradoSemiRigid,
    DoradoSpecialty,
    Apex,
    Bayside,
    Cobalt,
    Ember,
    Falcon,
}

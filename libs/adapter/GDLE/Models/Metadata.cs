using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Adapter.GDLE.Models;

public class Metadata
{
    public DateTime? LastModified { get; set; }
    public string ModifiedBy { get; set; }

    public List<ChangelogEntry> Changelog { get; set; }

    public string UserChangeSummary { get; set; }

    public bool IsDone { get; set; }

    public Metadata(LSDWeenie weenie)
    {
        LastModified = weenie.LastModified;
        ModifiedBy = weenie.ModifiedBy;

        Changelog = weenie.Changelog;

        UserChangeSummary = weenie.UserChangeSummary;

        IsDone = weenie.IsDone;
    }

    [JsonIgnore]
    public bool HasInfo
    {
        get
        {
            return LastModified != null
                || ModifiedBy != null
                || Changelog != null && Changelog.Count > 0
                || UserChangeSummary != null
                || IsDone;
        }
    }
}

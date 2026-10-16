using FlexCompanion.Flex;

static LicenseStatusDecoder.Record Read(string s)
{
    if (!LicenseStatusDecoder.TryDecode(s, out var v) || v is null)
        throw new Exception("Could not decode: " + s);
    return v;
}

void Assert(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS " + label);
}

var on = Read("license feature name=MULTIFLEX enabled=1 reason=BUILT_IN");
Assert(on.Type == LicenseStatusDecoder.Kind.Feature && on.Name == "MULTIFLEX"
       && on.Enabled == true && on.Reason == "BUILT_IN", "enabled feature");

var off = Read("license feature name=AUTO_TUNE enabled=0 reason=PLUS");
Assert(off.Enabled == false && off.Reason == "PLUS", "disabled feature");

var unknown = Read("license feature name=WFP reason=EA");
Assert(unknown.Enabled is null, "absent enabled field remains unknown");

var quoted = Read("license feature name=NOISE_REDUCTION enabled=1 reason=\"Subscription active\"");
Assert(quoted.Reason == "Subscription active", "quoted reason");

var sep = Read("license feature name=NOISE_FLOOR enabled=1 reason=Subscription\u007fvalid");
Assert(sep.Reason == "Subscription valid", "0x7F separator");

var sub = Read("license subscription name=SMARTSDR+ expiration=2026-10-10");
Assert(sub.Type == LicenseStatusDecoder.Kind.Subscription && sub.Name == "SMARTSDR+"
       && sub.Expiration == "2026-10-10", "subscription expiry");

var summary = Read("license highest_major_version=4 lowest_major_version=1 issued=2026-10-11");
Assert(summary.Type == LicenseStatusDecoder.Kind.Summary
       && summary.Fields["highest_major_version"] == "4", "major version summary");

Assert(!LicenseStatusDecoder.TryDecode("slice 0 in_use=1", out _), "unrelated status ignored");
Console.WriteLine("All read-only licence decoder tests passed.");

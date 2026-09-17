using System;
using System.Collections.Generic;

public static class AiRegionPolicy
{
    public const string SnapshotDate = "2026-09-17";

    private static readonly HashSet<string> ChatGpt = new HashSet<string>(
        ChatGptSupportedRegions.AllCodes, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GeminiWeb = new HashSet<string>(
        ("AX AL DZ AS AD AO AI AQ AG AR AM AW AU AT AZ BH BD BB BE BZ BJ BM BT BO BA BW BR IO VG BN BG BF BI " +
         "CV KH CM CA BQ KY CF TD CL CN CX CC CO KM CK CR CI HR CW CZ CD DK DJ DM DO EC EG SV GQ ER EE SZ ET FK " +
         "FO FJ FI FR GF PF TF GA GE DE GH GI GR GL GD GP GU GT GG GN GW GY HT HM HN HK HU IS IN ID IQ IE IM IL " +
         "IT JM JP JE JO KZ KE KI XK KW KG LA LV LB LS LR LY LI LT LU MO MG MW MY MV ML MT MH MQ MR MU YT MX FM " +
         "MD MC MN ME MS MA MZ MM NA NR NP NL NC NZ NI NE NG NU NF MK MP NO OM PK PW PS PA PG PY PE PH PN PL PT " +
         "PR QA CY CG RE RO RW BL SH KN LC MF PM VC WS SM ST SA SN RS SC SL SG SX SK SI SB SO ZA GS KR SS ES LK " +
         "SD SR SJ SE CH TW TJ TZ TH BS GM TL TG TK TO TT TN TR TM TC TV VI UG UA AE GB US UM UY UZ VU VA VE VN " +
         "WF EH YE ZM ZW").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);

    public static bool SupportsBoth(string countryCode)
    {
        if (String.IsNullOrWhiteSpace(countryCode)) return false;
        string code = countryCode.Trim();
        return ChatGpt.Contains(code) && GeminiWeb.Contains(code) &&
            !String.Equals(code, "CN", StringComparison.OrdinalIgnoreCase);
    }
}

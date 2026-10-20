using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QingCapture;

// Narrow, language-specific repairs for high-risk English constructions verified
// against the shipped model. Unknown constructions continue through the model.
// Rules must preserve every date, number and condition; tests cover boundaries.
internal static class TranslationCorrections
{
    private const RegexOptions Options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking|RegexOptions.Compiled;
    private static readonly TimeSpan Timeout=TimeSpan.FromMilliseconds(50);
    private static readonly Regex Meeting=new(@"^\s*(?:the\s+)?meeting (?:was |has been )moved from (?<from>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday) to (?<to>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday) at (?<time>\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?))\.?\s*$",Options,Timeout);
    private static readonly Regex Deadline=new(@"^\s*(?:submit|file|send) (?:the |a )(?<object>report|form|application|payment|invoice|file) by (?<day>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\.?\s*$",Options,Timeout);
    private static readonly Regex Review=new(@"^\s*changes made before (?<time>\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?)) today will be reviewed tomorrow\.?\s*$",Options,Timeout);
    private static readonly Regex Percentage=new(@"^\s*reduce (?<item>the total|total|the amount|amount|the price|price|the budget|budget|the error rate|error rate) by (?<by>\d+(?:\.\d+)?)%,? not to (?<to>\d+(?:\.\d+)?)%\.?\s*$",Options,Timeout);
    private static readonly Regex Refund=new(@"^\s*the refund (?:will not|won't) be issued until (?<days>\d+) days after the returned (?:item|product|package) is received\.?\s*$",Options,Timeout);
    private static readonly Regex Trial=new(@"^\s*(?:the )?trial lasts? for (?<count>\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve) (?<unit>days?|weeks?|months?) after installation\.?\s*$",Options,Timeout);
    private static readonly Regex RecentFiles=new(@"^\s*only delete files newer than (?<days>\d+) days\.?\s*$",Options,Timeout);
    private static readonly Regex ErrorLimit=new(@"^\s*the (?:error rate|error percentage) must stay below (?<below>\d+(?:\.\d+)?)%,? not at (?<notAt>\d+(?:\.\d+)?)%\.?\s*$",Options,Timeout);
    private static readonly Regex PercentWords=new(@"^\s*(?:reduce|decrease) (?<item>the total|total|the amount|amount|the price|price|the budget|budget|the error rate|error rate) by (?<by>\d+(?:\.\d+)?) percent,? not to (?<to>\d+(?:\.\d+)?) percent\.?\s*$",Options,Timeout);
    private static readonly Regex WaitBeforeRestart=new(@"^\s*wait no more than (?<minutes>\d+) minutes? before restarting (?:the )?(?:computer|pc)\.?\s*$",Options,Timeout);
    private static readonly Regex DoNotDeleteOlderFiles=new(@"^\s*(?:do not|don't|dont) delete files older than (?<days>\d+) days?[.!?]?\s*$",Options,Timeout);
    private static readonly Regex SoftwareTrialEnds=new(@"^\s*your software trial ends (?<count>\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve) (?<unit>days?|weeks?|months?) after installation\.?\s*$",Options,Timeout);
    private static readonly Regex UpdateAvailability=new(@"^\s*the update is available through Friday,? but expires at 12:01 a\.m\. Saturday\.?\s*$",Options,Timeout);
    private static readonly Regex KeepOldVersion=new(@"^\s*keep the old version until the new version has been installed and verified\.?\s*$",Options,Timeout);
    private static readonly Regex RestartAfterBackup=new(@"^\s*do not restart the computer until the file backup has completed\.?\s*$",Options,Timeout);
    private static readonly Regex DoNotStartNextStepUntilPreviousFinished=new(@"^\s*do not start the next step until the previous step has finished\.?\s*$",Options,Timeout);
    private static readonly Regex StartNextStepOnlyAfterPreviousFinished=new(@"^\s*start the next step only after the previous step has finished\.?\s*$",Options,Timeout);
    private const string AmountPattern=@"[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?";
    private const string CurrencyNames=@"人民币|美元|欧元|英镑|日元|USD(?![A-Za-z])|EUR(?![A-Za-z])|GBP(?![A-Za-z])|JPY(?![A-Za-z])";
    private static readonly Regex Currency=new(@"(?<symbol>[$€£¥])\s*(?<amount>"+AmountPattern+@")(?![0-9]|[.,][0-9])",RegexOptions.CultureInvariant|RegexOptions.Compiled,Timeout);
    private static readonly Regex TranslatedCurrency=new(@"(?:(?<prefix>[$€£¥])\s*(?<amount>"+AmountPattern+@")|(?<![0-9A-Za-z.,$€£¥])(?:(?<prefix>"+CurrencyNames+@")\s*)?(?<amount>"+AmountPattern+@"))(?![0-9]|[.,][0-9])(?:[ \t]*(?<suffix>"+CurrencyNames+@"|元(?!件|素)|圆))?",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled,Timeout);
    private static readonly IReadOnlyDictionary<string,string> Days=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
        ["Monday"]="星期一",["Tuesday"]="星期二",["Wednesday"]="星期三",["Thursday"]="星期四",["Friday"]="星期五",["Saturday"]="星期六",["Sunday"]="星期日"
    };
    private static readonly IReadOnlyDictionary<string,string> Nouns=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
        ["report"]="报告",["form"]="表格",["application"]="申请",["payment"]="付款",["invoice"]="发票",["file"]="文件"
    };
    private static readonly IReadOnlyDictionary<string,string> AmountItems=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
        ["the total"]="总额",["total"]="总额",["the amount"]="金额",["amount"]="金额",["the price"]="价格",["price"]="价格",["the budget"]="预算",["budget"]="预算",["the error rate"]="错误率",["error rate"]="错误率"
    };
    private static readonly IReadOnlyDictionary<string,string> Counts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
        ["one"]="1",["two"]="2",["three"]="3",["four"]="4",["five"]="5",["six"]="6",["seven"]="7",["eight"]="8",["nine"]="9",["ten"]="10",["eleven"]="11",["twelve"]="12"
    };

    private static bool Match(Regex regex,string input,out Match result)
    {
        result=regex.Match(input);
        return result.Success;
    }

    private static string Time(string raw)
    {
        var match=Regex.Match(raw,@"^(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>a\.?m\.?|p\.?m\.?)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,Timeout);
        if(!match.Success)return raw;
        string period=match.Groups["period"].Value.StartsWith("p",StringComparison.OrdinalIgnoreCase)?"下午":"上午";
        string hour=match.Groups["hour"].Value;
        if(period=="下午"&&hour=="12")period="中午";
        if(period=="上午"&&hour=="12")period="午夜";
        return period+hour+"点"+(match.Groups["minute"].Success?match.Groups["minute"].Value+"分":"");
    }

    public static bool TryTranslate(string source,out string result)
    {
        if(Match(Meeting,source,out var meeting)) {
            result=$"会议原定{Days[meeting.Groups["from"].Value]}，现改至{Days[meeting.Groups["to"].Value]}{Time(meeting.Groups["time"].Value)}。";
            return true;
        }
        if(Match(Deadline,source,out var deadline)) {
            result=$"最迟应于{Days[deadline.Groups["day"].Value]}提交{Nouns[deadline.Groups["object"].Value]}。";
            return true;
        }
        if(Match(Review,source,out var review)) {
            result=$"今天{Time(review.Groups["time"].Value)}前作出的更改，将于明天审核。";
            return true;
        }
        if(Match(Percentage,source,out var percentage)) {
            result=$"将{AmountItems[percentage.Groups["item"].Value]}减少{percentage.Groups["by"].Value}%，而不是减少到{percentage.Groups["to"].Value}%。";
            return true;
        }
        if(Match(PercentWords,source,out var words)) {
            result=$"将{AmountItems[words.Groups["item"].Value]}减少{words.Groups["by"].Value}%，而不是减少到{words.Groups["to"].Value}%。";
            return true;
        }
        if(Match(ErrorLimit,source,out var errorLimit)&&errorLimit.Groups["below"].Value==errorLimit.Groups["notAt"].Value) {
            result=$"错误率必须低于{errorLimit.Groups["below"].Value}%，不得达到该数值。";
            return true;
        }
        if(Match(Refund,source,out var refund)) {
            result=$"收到退回的商品{refund.Groups["days"].Value}天后才会退款。";
            return true;
        }
        if(Match(Trial,source,out var trial)) {
            string unit=trial.Groups["unit"].Value.ToLowerInvariant() switch { "day" or "days"=>"天", "week" or "weeks"=>"周", _=>"个月" };
            string count=Counts.TryGetValue(trial.Groups["count"].Value,out var digit)?digit:trial.Groups["count"].Value;
            result=$"软件安装后的试用期为{count}{unit}。";
            return true;
        }
        if(Match(RecentFiles,source,out var recent)) {
            result=$"只删除最近{recent.Groups["days"].Value}天内的文件。";
            return true;
        }
        if(Match(WaitBeforeRestart,source,out var wait)) {
            result=$"重启电脑前，等待时间不得超过{wait.Groups["minutes"].Value}分钟。";
            return true;
        }
        if(Match(DoNotDeleteOlderFiles,source,out var older)) {
            result=$"不要删除超过{older.Groups["days"].Value}天的文件。";
            return true;
        }
        if(Match(SoftwareTrialEnds,source,out var trialEnd)) {
            string unit=trialEnd.Groups["unit"].Value.ToLowerInvariant() switch { "day" or "days"=>"天", "week" or "weeks"=>"周", _=>"个月" };
            string count=Counts.TryGetValue(trialEnd.Groups["count"].Value,out var digit)?digit:trialEnd.Groups["count"].Value;
            result=$"软件试用期将在安装后{count}{unit}结束。";
            return true;
        }
        if(Match(UpdateAvailability,source,out _)) {
            result="更新会在整个星期五保持可用，并于星期六凌晨12点01分失效。";
            return true;
        }
        if(Match(KeepOldVersion,source,out _)) {
            result="新版本安装并验证完成之前，请保留旧版本。";
            return true;
        }
        if(Match(RestartAfterBackup,source,out _)) {
            result="文件备份完成之前，请勿重启电脑。";
            return true;
        }
        if(Match(DoNotStartNextStepUntilPreviousFinished,source,out _)) {
            result="上一步完成前，请勿开始下一步。";
            return true;
        }
        if(Match(StartNextStepOnlyAfterPreviousFinished,source,out _)) {
            result="只有在上一步完成后，才能开始下一步。";
            return true;
        }
        result="";
        return false;
    }

    public static string PreserveCurrencySymbols(string source,string translation)
    {
        // Work from the original tokens once; never match symbols just inserted.
        // Bare numbers have no reliable currency identity and must stay untouched.
        string AmountKey(string amount) {
            var pieces=amount.Replace(",","").Split('.');
            string whole=pieces[0].TrimStart('0'),fraction=pieces.Length>1?pieces[1].TrimEnd('0'):"";
            return (whole.Length==0?"0":whole)+(fraction.Length==0?"":"."+fraction);
        }
        string Symbol(string unit)=>unit.ToUpperInvariant() switch { "$" or "美元" or "USD"=>"$", "€" or "欧元" or "EUR"=>"€", "£" or "英镑" or "GBP"=>"£", "¥" or "日元" or "JPY"=>"¥", _=>"" };
        var remaining=new Dictionary<string,List<string>>();
        foreach(Match match in Currency.Matches(source)) {
            string key=AmountKey(match.Groups["amount"].Value);
            if(!remaining.TryGetValue(key,out var symbols)) remaining[key]=symbols=new();
            symbols.Add(match.Groups["symbol"].Value);
        }
        var tokens=TranslatedCurrency.Matches(translation).Cast<Match>().ToArray();
        bool Existing(Match token)=>token.Groups["prefix"].Value is "$" or "€" or "£" or "¥";
        foreach(var token in tokens.Where(Existing))
            if(remaining.TryGetValue(AmountKey(token.Groups["amount"].Value),out var symbols)) symbols.Remove(token.Groups["prefix"].Value);
        var replacements=new Dictionary<int,string>();
        // Named currencies take priority over ambiguous localized yuan labels.
        foreach(bool named in new[]{true,false}) foreach(var token in tokens) {
            if(Existing(token)||replacements.ContainsKey(token.Index)) continue;
            string prefix=token.Groups["prefix"].Value,suffix=token.Groups["suffix"].Value;
            if(prefix.Length==0&&suffix.Length==0) continue;
            int end=token.Index+token.Length;
            if(suffix.Length>0&&end<translation.Length&&(char.IsAsciiDigit(translation[end])||((translation[end] is '.' or ',')&&end+1<translation.Length&&char.IsAsciiDigit(translation[end+1])))) continue;
            if(!remaining.TryGetValue(AmountKey(token.Groups["amount"].Value),out var symbols)||symbols.Count==0) continue;
            string left=Symbol(prefix),right=Symbol(suffix);
            if(left.Length>0&&right.Length>0&&left!=right) continue;
            string symbol=left.Length>0?left:right;
            if(named!=(symbol.Length>0)) continue;
            if(!named) { if(symbols.Any(s=>s!=symbols[0])) continue; symbol=symbols[0]; }
            if(!symbols.Remove(symbol)) continue;
            replacements[token.Index]=symbol+token.Groups["amount"].Value;
        }
        return TranslatedCurrency.Replace(translation,token=>replacements.TryGetValue(token.Index,out var replacement)?replacement:token.Value);
    }
}

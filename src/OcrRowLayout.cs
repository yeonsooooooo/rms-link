namespace RmsLink;

public sealed record ScreenWord(string Text,double X,double Y,double Width,double Height);
public static class OcrRowLayout
{
    // OCR often returns each table column as a separate line. Rebuild physical rows.
    // Multiple room cards on one row remain ambiguous and are rejected by EventParser.
    public static List<string> Join(IEnumerable<ScreenWord> words)
    {
        var rows=new List<List<ScreenWord>>();
        foreach(var word in words.Where(w=>!string.IsNullOrWhiteSpace(w.Text)&&w.Height>0).OrderBy(w=>w.Y+w.Height/2).ThenBy(w=>w.X)) {
            var row=rows.LastOrDefault();
            if(row==null || Math.Abs(row[0].Y+row[0].Height/2-word.Y-word.Height/2)>Math.Min(row[0].Height,word.Height)*0.55) {
                row=new(); rows.Add(row);
            }
            row.Add(word);
        }
        return rows.Select(row=>string.Join(" ",row.OrderBy(w=>w.X).Select(w=>w.Text))).ToList();
    }
}

public static class AccessibleRowLayout
{
    public static string Join(string name,IEnumerable<string> cells)
    {
        var values=cells.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        // Some providers repeat the complete row in Name as well as exposing its cells.
        if(!string.IsNullOrWhiteSpace(name) && values.All(v=>name.Contains(v,StringComparison.Ordinal)))return name;
        return string.Join(" ",new[]{name}.Concat(values).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct());
    }
}

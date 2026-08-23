namespace RmsLink;

/// <summary>전체 화면을 덮는 반투명 오버레이에서 드래그로 영역 선택</summary>
public sealed class RoiSelectorForm : Form
{
    private Point _start;
    private Rectangle _sel;
    private bool _dragging;

    public Rectangle SelectedScreenRect { get; private set; }

    public RoiSelectorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.Black;
        Opacity = 0.4;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragging = true;
        _start = e.Location;
        _sel = new Rectangle(e.Location, Size.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _sel = Rectangle.FromLTRB(
            Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
            Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        if (_sel.Width >= 20 && _sel.Height >= 20)
        {
            SelectedScreenRect = new Rectangle(
                Bounds.X + _sel.X, Bounds.Y + _sel.Y, _sel.Width, _sel.Height);
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _sel = Rectangle.Empty;
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var font = new Font("맑은 고딕", 16, FontStyle.Bold);
        const string guide = "RMS의 이벤트 로그 영역(객실/이벤트/시각 목록)을 마우스로 드래그하세요.  [ESC] 취소";
        var sz = g.MeasureString(guide, font);
        g.FillRectangle(Brushes.Black, (Width - sz.Width) / 2 - 12, 28, sz.Width + 24, sz.Height + 12);
        g.DrawString(guide, font, Brushes.White, (Width - sz.Width) / 2, 34);

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            using var pen = new Pen(Color.Red, 3);
            g.DrawRectangle(pen, _sel);
            using var f2 = new Font("맑은 고딕", 11, FontStyle.Bold);
            g.DrawString($"{_sel.Width} x {_sel.Height}", f2, Brushes.Yellow,
                _sel.X, Math.Max(0, _sel.Y - 24));
        }
    }
}

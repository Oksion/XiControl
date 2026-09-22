namespace XiControl.Ui.Settings;

/// <summary>
/// Базовая панель вкладки настроек: вертикальный поток карточек с прокруткой.
/// Каждая вкладка — самостоятельный контрол, собирающий себя в конструкторе;
/// SettingsForm только хостит и переключает видимость.
/// </summary>
public abstract class SettingsPane : FlowLayoutPanel
{
    protected readonly SettingsToolkit Ui;

    protected SettingsPane(SettingsToolkit ui)
    {
        Ui = ui;
        Dock = DockStyle.Fill;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = ui.T.WinBg;
        Padding = new Padding(ui.Sc(26), ui.Sc(18), ui.Sc(26), ui.Sc(24));
        Tag = "pane";
    }

    /// <summary>
    /// Не подкручивать список под контрол, получивший фокус МЫШЬЮ. Штатное поведение
    /// ScrollableControl — доскроллить до фокусируемого контрола целиком; для клавиатуры это
    /// правильно (иначе Tab уводит фокус за край экрана), а для мыши — вредно: наполовину
    /// видимая карточка на нажатии подпрыгивает под курсором, и отпускание кнопки приходится
    /// уже мимо контрола. Со стороны это выглядит как «первый клик не срабатывает».
    ///
    /// Различаем по зажатой кнопке мыши: пока она нажата, фокус пришёл от клика.
    /// </summary>
    protected override Point ScrollToControl(Control activeControl) =>
        MouseButtons == MouseButtons.None ? base.ScrollToControl(activeControl) : AutoScrollPosition;
}

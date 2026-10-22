using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace AutoHubPro;

public partial class MainWindow : Window
{
    public sealed class ServiceRecord
    {
        public DateTime Date { get; set; } = DateTime.Today;
        public string CarModel { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Mileage { get; set; }
        public int? NextServiceMileage { get; set; }
        public decimal Amount { get; set; }
        public decimal? FuelConsumption { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public sealed class MonthlyExpense
    {
        public string MonthLabel { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public string AmountLabel { get; init; } = string.Empty;
        public string AmountTooltip { get; init; } = string.Empty;
        public double BarHeight { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ObservableCollection<ServiceRecord> _records = new();
    private readonly ICollectionView _recordsView;
    private readonly string _storagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoHubPro",
        "records.json");
    private ServiceRecord? _editingRecord;
    private bool _storageLoadFailed;

    public MainWindow()
    {
        InitializeComponent();
        DateRecord.SelectedDate = DateTime.Today;
        _recordsView = CollectionViewSource.GetDefaultView(_records);
        _recordsView.Filter = MatchesSearch;
        GridRecords.ItemsSource = _recordsView;
        MonthlyChart.ItemsSource = Array.Empty<MonthlyExpense>();
        ApplySortOrder();
        LoadRecords();
        UpdateSummary();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not ServiceRecord record)
        {
            return false;
        }

        var selectedCategory = (CmbCategoryFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var matchesCategory = string.IsNullOrEmpty(selectedCategory)
            || selectedCategory == "Усі події"
            || string.Equals(record.Category, selectedCategory, StringComparison.CurrentCultureIgnoreCase);
        var query = TxtSearch?.Text.Trim();
        return matchesCategory && (string.IsNullOrEmpty(query)
            || record.CarModel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || record.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || record.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || record.Mileage.ToString(CultureInfo.CurrentCulture).Contains(
                query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void LoadRecords()
    {
        if (!File.Exists(_storagePath))
        {
            return;
        }

        try
        {
            var loadedRecords = JsonSerializer.Deserialize<List<ServiceRecord>>(
                File.ReadAllText(_storagePath), JsonOptions);
            if (loadedRecords is null || loadedRecords.Any(record => !IsValidRecord(record)))
            {
                throw new JsonException("Файл містить порожні або некоректні записи.");
            }

            foreach (var record in loadedRecords)
            {
                _records.Add(record);
            }
        }
        catch (IOException exception)
        {
            _storageLoadFailed = true;
            ShowStorageError("Не вдалося прочитати збережений журнал.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _storageLoadFailed = true;
            ShowStorageError("Немає дозволу прочитати збережений журнал.", exception);
        }
        catch (JsonException exception)
        {
            _storageLoadFailed = true;
            ShowStorageError("Файл журналу пошкоджений або має невідомий формат.", exception);
        }
    }

    private static bool IsValidRecord(ServiceRecord? record)
    {
        return record is not null
            && !string.IsNullOrWhiteSpace(record.CarModel)
            && !string.IsNullOrWhiteSpace(record.Category)
            && !string.IsNullOrWhiteSpace(record.Description)
            && record.Mileage >= 0
            && (record.NextServiceMileage is null
                || (record.NextServiceMileage > record.Mileage
                    && string.Equals(record.Category, "Техобслуговування", StringComparison.Ordinal)))
            && record.Amount >= 0
            && (record.FuelConsumption is null
                || record.FuelConsumption is > 0 and <= 100)
            && record.Date != default;
    }

    private void BtnAddRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateRecord(out var record))
        {
            return;
        }

        if (_editingRecord is null)
        {
            _records.Add(record);
        }
        else
        {
            var index = _records.IndexOf(_editingRecord);
            if (index < 0)
            {
                MessageBox.Show("Не вдалося знайти запис для редагування. Оновіть журнал і спробуйте ще раз.",
                    "Запис не знайдено", MessageBoxButton.OK, MessageBoxImage.Warning);
                ResetEditor();
                return;
            }

            _records[index] = record;
        }

        ResetEditor();
        UpdateSummary();
        SaveRecords();
    }

    private bool TryCreateRecord(out ServiceRecord record)
    {
        record = new ServiceRecord();
        var carModel = TxtCarModel.Text.Trim();
        var category = (CmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var description = TxtDescription.Text.Trim();

        if (string.IsNullOrWhiteSpace(carModel))
        {
            return ShowInputError("Введіть марку та модель автомобіля.", TxtCarModel);
        }

        if (!int.TryParse(TxtMileage.Text, NumberStyles.None, CultureInfo.CurrentCulture, out var mileage)
            || mileage < 0)
        {
            return ShowInputError("Пробіг має бути цілим невід'ємним числом.", TxtMileage);
        }

        int? nextServiceMileage = null;
        if (!string.IsNullOrWhiteSpace(TxtNextServiceMileage.Text))
        {
            if (!int.TryParse(TxtNextServiceMileage.Text, NumberStyles.None,
                    CultureInfo.CurrentCulture, out var parsedNextServiceMileage)
                || parsedNextServiceMileage <= mileage)
            {
                return ShowInputError(
                    "Плановий пробіг наступного ТО має бути цілим числом більшим за поточний пробіг.",
                    TxtNextServiceMileage);
            }

            if (!string.Equals(category, "Техобслуговування", StringComparison.Ordinal))
            {
                return ShowInputError("План наступного ТО можна додати до події «Техобслуговування».",
                    CmbCategory);
            }

            nextServiceMileage = parsedNextServiceMileage;
        }

        if (!TryParseDecimal(TxtAmount.Text, out var amount) || amount < 0)
        {
            return ShowInputError("Витрати мають бути невід'ємним числом.", TxtAmount);
        }

        decimal? consumption = null;
        if (!string.IsNullOrWhiteSpace(TxtFuelConsumption.Text))
        {
            if (!TryParseDecimal(TxtFuelConsumption.Text, out var parsedConsumption)
                || parsedConsumption <= 0 || parsedConsumption > 100)
            {
                return ShowInputError("Витрата має бути числом більше 0 і не більше 100 л/100 км.",
                    TxtFuelConsumption);
            }

            consumption = parsedConsumption;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return ShowInputError("Додайте короткий опис події.", TxtDescription);
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return ShowInputError("Оберіть тип події.", CmbCategory);
        }

        record = new ServiceRecord
        {
            Date = DateRecord.SelectedDate ?? DateTime.Today,
            CarModel = carModel,
            Category = category,
            Mileage = mileage,
            NextServiceMileage = nextServiceMileage,
            Amount = amount,
            FuelConsumption = consumption,
            Description = description
        };
        return true;
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingWhite
            | NumberStyles.AllowTrailingWhite
            | NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint;

        return decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out value)
            || decimal.TryParse(text.Replace(',', '.'), styles, CultureInfo.InvariantCulture, out value);
    }

    private static bool ShowInputError(string message, Control control)
    {
        MessageBox.Show(message, "Перевірте введені дані",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        control.Focus();
        return false;
    }

    private void BtnCalculateTrip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDecimal(TxtTripDistance.Text, out var distance) || distance <= 0)
        {
            ShowInputError("Відстань має бути числом більшим за 0 км.", TxtTripDistance);
            return;
        }

        if (!TryParseDecimal(TxtFuelConsumption.Text, out var consumption)
            || consumption <= 0 || consumption > 100)
        {
            ShowInputError("Введіть витрату пального від 0 до 100 л/100 км.", TxtFuelConsumption);
            return;
        }

        if (!TryParseDecimal(TxtFuelPrice.Text, out var fuelPrice) || fuelPrice < 0)
        {
            ShowInputError("Ціна пального має бути невід'ємним числом.", TxtFuelPrice);
            return;
        }

        var requiredFuel = distance / 100m * consumption;
        var totalCost = requiredFuel * fuelPrice;
        LblTripResult.Text = $"Потрібно {requiredFuel:N1} л пального.\nОрієнтовна вартість: {totalCost:N2} грн";
    }

    private void BtnDeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        if (GridRecords.SelectedItem is not ServiceRecord selectedRecord)
        {
            MessageBox.Show("Спочатку виберіть запис у журналі.", "Немає вибраного запису",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Видалити запис «{selectedRecord.Description}»?",
            "Підтвердження видалення",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _records.Remove(selectedRecord);
        if (ReferenceEquals(_editingRecord, selectedRecord))
        {
            ResetEditor();
        }

        UpdateSummary();
        SaveRecords();
    }

    private void GridRecords_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridRecords.SelectedItem is not ServiceRecord record)
        {
            return;
        }

        _editingRecord = record;
        TxtCarModel.Text = record.CarModel;
        DateRecord.SelectedDate = record.Date;
        CmbCategory.SelectedItem = CmbCategory.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), record.Category,
                StringComparison.Ordinal));
        TxtMileage.Text = record.Mileage.ToString(CultureInfo.CurrentCulture);
        TxtNextServiceMileage.Text = record.NextServiceMileage?.ToString(CultureInfo.CurrentCulture)
            ?? string.Empty;
        TxtAmount.Text = record.Amount.ToString(CultureInfo.CurrentCulture);
        TxtFuelConsumption.Text = record.FuelConsumption?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        TxtDescription.Text = record.Description;
        BtnAddRecord.Content = "Зберегти зміни";
        TxtCarModel.Focus();
    }

    private void ResetEditor()
    {
        _editingRecord = null;
        DateRecord.SelectedDate = DateTime.Today;
        TxtMileage.Clear();
        TxtNextServiceMileage.Clear();
        TxtAmount.Clear();
        TxtFuelConsumption.Clear();
        TxtDescription.Clear();
        CmbCategory.SelectedIndex = 0;
        BtnAddRecord.Content = "＋  Додати до журналу";
        GridRecords.SelectedItem = null;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _recordsView?.Refresh();
    }

    private void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        _recordsView?.Refresh();
        ApplySortOrder();
    }

    private void BtnResetFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Clear();
        CmbCategoryFilter.SelectedIndex = 0;
        CmbSortOrder.SelectedIndex = 0;
        _recordsView.Refresh();
        ApplySortOrder();
    }

    private void ApplySortOrder()
    {
        if (_recordsView is null || CmbSortOrder?.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        var sortOption = selectedItem.Content?.ToString();
        var (propertyName, direction) = sortOption switch
        {
            "Старі спочатку" => (nameof(ServiceRecord.Date), ListSortDirection.Ascending),
            "Пробіг: більший спочатку" => (nameof(ServiceRecord.Mileage), ListSortDirection.Descending),
            "Витрати: більші спочатку" => (nameof(ServiceRecord.Amount), ListSortDirection.Descending),
            _ => (nameof(ServiceRecord.Date), ListSortDirection.Descending)
        };

        using (_recordsView.DeferRefresh())
        {
            _recordsView.SortDescriptions.Clear();
            _recordsView.SortDescriptions.Add(new SortDescription(propertyName, direction));
        }
    }

    private void UpdateSummary()
    {
        LblTotalRecords.Text = _records.Count.ToString(CultureInfo.CurrentCulture);
        var now = DateTime.Now;
        var monthlyTotal = _records
            .Where(record => record.Date.Month == now.Month && record.Date.Year == now.Year)
            .Sum(record => record.Amount);
        LblMonthlyTotal.Text = $"{monthlyTotal:N2} грн";
        UpdateMonthlyChart(now);

        var consumptions = _records
            .Where(record => record.FuelConsumption.HasValue)
            .Select(record => record.FuelConsumption!.Value)
            .ToList();
        LblAvgConsumption.Text = consumptions.Count == 0
            ? "— л/100 км"
            : $"{consumptions.Average():N1} л/100 км";
        UpdateMaintenanceReminder();
    }

    private void UpdateMaintenanceReminder()
    {
        var reminder = _records
            .Where(record => record.NextServiceMileage.HasValue
                && string.Equals(record.Category, "Техобслуговування", StringComparison.Ordinal))
            .GroupBy(record => record.CarModel, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var plan = group.OrderByDescending(record => record.Date)
                    .ThenByDescending(record => record.Mileage)
                    .First();
                var currentMileage = _records
                    .Where(record => string.Equals(record.CarModel, plan.CarModel,
                        StringComparison.CurrentCultureIgnoreCase))
                    .Max(record => record.Mileage);
                return new
                {
                    plan.CarModel,
                    RemainingKilometers = (long)plan.NextServiceMileage!.Value - currentMileage
                };
            })
            .OrderBy(item => item.RemainingKilometers)
            .FirstOrDefault();

        if (reminder is null)
        {
            LblMaintenanceReminder.Text = "ТО: додайте план пробігу до запису";
            LblMaintenanceReminder.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        if (reminder.RemainingKilometers <= 0)
        {
            LblMaintenanceReminder.Text =
                $"ТО {reminder.CarModel}: прострочено на {Math.Abs(reminder.RemainingKilometers):N0} км";
            LblMaintenanceReminder.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        else if (reminder.RemainingKilometers <= 500)
        {
            LblMaintenanceReminder.Text =
                $"ТО {reminder.CarModel}: скоро, через {reminder.RemainingKilometers:N0} км";
            LblMaintenanceReminder.Foreground = System.Windows.Media.Brushes.Gold;
        }
        else
        {
            LblMaintenanceReminder.Text =
                $"ТО {reminder.CarModel}: через {reminder.RemainingKilometers:N0} км";
            LblMaintenanceReminder.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
    }

    private void UpdateMonthlyChart(DateTime currentDate)
    {
        var firstMonth = new DateTime(currentDate.Year, currentDate.Month, 1).AddMonths(-5);
        var monthlyAmounts = Enumerable.Range(0, 6)
            .Select(offset =>
            {
                var month = firstMonth.AddMonths(offset);
                var nextMonth = month.AddMonths(1);
                var amount = _records
                    .Where(record => record.Date >= month && record.Date < nextMonth)
                    .Sum(record => record.Amount);
                return (Month: month, Amount: amount);
            })
            .ToList();
        var maximumAmount = monthlyAmounts.Max(month => month.Amount);
        var culture = CultureInfo.CurrentCulture;

        MonthlyChart.ItemsSource = monthlyAmounts.Select(month => new MonthlyExpense
        {
            MonthLabel = culture.TextInfo.ToTitleCase(month.Month.ToString("MMM", culture)),
            Amount = month.Amount,
            AmountLabel = month.Amount == 0 ? "—" : month.Amount.ToString("N0", culture),
            AmountTooltip = $"{month.Month.ToString("MMMM yyyy", culture)}: {month.Amount:N2} грн",
            BarHeight = maximumAmount == 0
                ? 2
                : Math.Max(2, (double)(month.Amount / maximumAmount) * 76)
        }).ToList();
    }

    private void SaveRecords()
    {
        if (_storageLoadFailed)
        {
            MessageBox.Show(
                "Автозбереження вимкнене, щоб не перезаписати пошкоджений файл. " +
                "Експортуйте журнал у новий JSON-файл і відновіть його через «Імпорт JSON».",
                "Потрібне відновлення файлу",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(_records, JsonOptions));
        }
        catch (IOException exception)
        {
            ShowStorageError("Не вдалося автоматично зберегти журнал.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowStorageError("Немає дозволу зберегти журнал.", exception);
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Експортувати автомобільний журнал",
            Filter = "Файл JSON (*.json)|*.json",
            FileName = $"AutoHubPro-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_records, JsonOptions));
            MessageBox.Show("Журнал успішно експортовано.", "Експорт завершено",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (IOException exception)
        {
            ShowStorageError("Не вдалося експортувати журнал.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowStorageError("Немає дозволу записати файл експорту.", exception);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Імпортувати автомобільний журнал",
            Filter = "Файл JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        List<ServiceRecord>? importedRecords;
        try
        {
            importedRecords = JsonSerializer.Deserialize<List<ServiceRecord>>(
                File.ReadAllText(dialog.FileName), JsonOptions);
        }
        catch (IOException exception)
        {
            ShowStorageError("Не вдалося прочитати файл імпорту.", exception);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowStorageError("Немає дозволу прочитати файл імпорту.", exception);
            return;
        }
        catch (JsonException exception)
        {
            ShowStorageError("Файл імпорту пошкоджений або має невідомий формат.", exception);
            return;
        }

        if (importedRecords is null || importedRecords.Any(record => !IsValidRecord(record)))
        {
            MessageBox.Show("Файл не містить коректного списку автомобільних записів.",
                "Помилка імпорту", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show($"Замінити поточний журнал записами з файлу ({importedRecords.Count})?",
                "Підтвердження імпорту", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        _records.Clear();
        foreach (var record in importedRecords)
        {
            _records.Add(record);
        }

        _storageLoadFailed = false;
        ResetEditor();
        _recordsView.Refresh();
        UpdateSummary();
        SaveRecords();
        MessageBox.Show("Журнал успішно імпортовано.", "Імпорт завершено",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ShowStorageError(string message, Exception exception)
    {
        MessageBox.Show($"{message}\n\nДеталі: {exception.Message}",
            "Помилка роботи з файлом", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

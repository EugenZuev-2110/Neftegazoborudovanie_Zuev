using System;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Neftegazoborudovanie_Zuev
{
    public partial class MainWindow : Window
    {
        private bool _isPolling = false;
        private SerialPort _port;

        public MainWindow()
        {
            InitializeComponent();
            // Находим все доступные COM-порты в системе
            PortComboBox.ItemsSource = SerialPort.GetPortNames();
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isPolling) return;

            string selectedPort = PortComboBox.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedPort)) return;

            _isPolling = true;
            BtnConnect.IsEnabled = false;

            // Запускаем бесконечный цикл опроса в фоновом потоке, чтобы UI не зависал
            await Task.Run(() => StartModbusCycle(selectedPort));
        }

        private void StartModbusCycle(string portName)
        {
            // Настройка порта согласно заданию: 115200, 8 бит данных, без четности (N), 1 стоп-бит
            using (_port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One))
            {
                try
                {
                    _port.ReadTimeout = 500; // Ждем ответа от устройства не более 0.5 сек
                    _port.Open();

                    while (_isPolling)
                    {
                        // ШАГ 1: Запись в регистр 1150 (0x047E) значения 1
                        // Структура: [ID=1] [Func=06] [Addr_Hi=04] [Addr_Lo=7E] [Data_Hi=00] [Data_Lo=01] [CRC_Lo] [CRC_Hi]
                        SendModbusRequest(new byte[] { 0x01, 0x06, 0x04, 0x7E, 0x00, 0x01 });

                        // Небольшая пауза для обработки устройством
                        Task.Delay(50).Wait();

                        // ШАГ 2: Чтение регистра 1150 (проверяем, сброшен ли он в 0)
                        // Запрос (Func 03, 1 регистр): 01 03 04 7E 00 01 + CRC
                        byte[] statusResponse = SendModbusRequest(new byte[] { 0x01, 0x03, 0x04, 0x7E, 0x00, 0x01 }, 7);

                        if (statusResponse != null && statusResponse[4] == 0) // Если значение регистра (байт 4) равно 0
                        {
                            // ШАГ 3: Чтение 200 байт с адреса 1152 (0x0480)
                            // 200 байт = 100 регистров (0x0064)
                            // Ожидаемый ответ: 5 байт заголовка + 200 байт данных + 2 байта CRC = 207 байт
                            byte[] dataResponse = SendModbusRequest(new byte[] { 0x01, 0x03, 0x04, 0x80, 0x00, 0x64 }, 207);

                            if (dataResponse != null)
                            {
                                // Извлекаем полезные 200 байт (пропускаем ID, Func и ByteCount)
                                byte[] payload = dataResponse.Skip(3).Take(200).ToArray();
                                // Преобразуем в знаковые байты (sbyte) для графика
                                double[] plotPoints = payload.Select(b => (double)(sbyte)b).ToArray();

                                Dispatcher.Invoke(() => UpdateGraph(plotPoints));
                            }
                        }

                        // ШАГ 4: Чтение Float с адреса 1354 (0x054A), 2 регистра
                        byte[] floatResponse = SendModbusRequest(new byte[] { 0x01, 0x03, 0x05, 0x4A, 0x00, 0x02 }, 9);
                        if (floatResponse != null)
                        {
                            // Собираем float из байтов ответа (байты 3,4,5,6)
                            float val = BitConverter.ToSingle(new byte[] { floatResponse[4], floatResponse[3], floatResponse[6], floatResponse[5] }, 0);
                            Dispatcher.Invoke(() => TxtFloatValue.Text = val.ToString("F2"));
                        }

                        Task.Delay(200).Wait(); // Интервал опроса
                    }
                }
                catch (Exception ex)
                {
                    _isPolling = false;
                    Dispatcher.Invoke(() => MessageBox.Show("Ошибка: " + ex.Message));
                }
            }
        }

        // Вспомогательный метод: добавляет CRC, отправляет и читает ответ
        private byte[] SendModbusRequest(byte[] frame, int expectedLength = 8)
        {
            byte[] fullFrame = AddCRC(frame);
            _port.Write(fullFrame, 0, fullFrame.Length);

            byte[] buffer = new byte[expectedLength];
            int offset = 0;
            try
            {
                // Читаем из порта, пока не получим нужное кол-во байт
                while (offset < expectedLength)
                {
                    int count = _port.Read(buffer, offset, expectedLength - offset);
                    offset += count;
                }
                return buffer;
            }
            catch { return null; } // Тайм-аут или ошибка связи
        }

        // Алгоритм расчета контрольной суммы CRC16 для Modbus
        private byte[] AddCRC(byte[] data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc >>= 1;
                }
            }
            byte[] result = new byte[data.Length + 2];
            Array.Copy(data, result, data.Length);
            result[data.Length] = (byte)(crc & 0xFF);         // Low byte
            result[data.Length + 1] = (byte)(crc >> 8);      // High byte
            return result;
        }

        private void UpdateGraph(double[] data)
        {
            WpfPlot1.Plot.Clear();
            WpfPlot1.Plot.AddSignal(data);
            WpfPlot1.Plot.AxisAuto();
            WpfPlot1.Refresh();
        }
    }
}

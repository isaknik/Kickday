using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Logging;
using StockSharp.Messages;

namespace PrismaBoy
{
    sealed class KickDay: MyBaseStrategy
    {
        private readonly decimal _kickPercent;                                          // Процент ударного дня
        private readonly TimeOfDay _timeOff;                                            // Время отсечки
        private readonly TimeOfDay _timeToStop;                                         // Время остановки стратегии

        private DateTime _nextTimeToPlaceOrdersIfKickDay;                               // Время следующей проверки на условия ударного дня

        public KickDay(List<Security> securityList, Dictionary<string, decimal> securityVolumeDictionary, TimeSpan timeFrame, decimal stopLossPercent, decimal takeProfitPercent, decimal kickPercent, TimeOfDay timeOff, TimeOfDay timeToStop)
            : base(securityList, securityVolumeDictionary, timeFrame, stopLossPercent, takeProfitPercent)
        {
            Name = "KickDay";
            IsIntraDay = true;
            TimeToStartRobot.Hours = 16;
            TimeToStartRobot.Minutes = 30;

            // В соответствии с параметрами конструктора
            _kickPercent = kickPercent;
            _timeOff = timeOff;
            _timeToStop = timeToStop;

            // Объявляем и инициализируем пустые переменные
            switch (DateTime.Today.DayOfWeek)
            {
                case DayOfWeek.Saturday:
                    _nextTimeToPlaceOrdersIfKickDay =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(2);
                    break;

                case DayOfWeek.Sunday:
                    _nextTimeToPlaceOrdersIfKickDay =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(1);
                    break;

                default:
                    _nextTimeToPlaceOrdersIfKickDay = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes);
                    break;
            }
        }

        /// <summary>
        /// Событие старта стратегии
        /// </summary>
        protected override void OnStarted()
        {
            TimeToStopRobot = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, _timeToStop.Hours,
                                           _timeToStop.Minutes, 00);

            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToPlaceOrdersIfKickDay)
                .Do(PlaceOrdersIfKickDay)
                .Once()
                .Apply(this);

            this.AddInfoLog("Стратегия запускается со следующими параметрами:" +
                            "\nВремя отсечки: " + _nextTimeToPlaceOrdersIfKickDay +
                            "\nУдар, %: " + _kickPercent +
                            "\nСтоплосс, %: " + StopLossPercent);

            var prevEveningPrices = MainWindow.Instance.ReadEveningPrices(1);
            var lastEveningInfo = SecurityList.Aggregate("Данные на вечерний клиринг предыдущего дня:\n\n",
                                                         (current, security) =>
                                                         current +
                                                         (security.Code + " - " + prevEveningPrices[security.Code].Price +
                                                          " at " +
                                                          prevEveningPrices[
                                                              security.Code
                                                              ].Time.ToString(
                                                                  CultureInfo.
                                                                      CurrentCulture) +
                                                          "\n"));

            this.AddInfoLog(lastEveningInfo);

            base.OnStarted();
        }

        /// <summary>
        /// Обработчик события прихода _timeOff времени и установки лимит ордера, если ударный день
        /// </summary>
        private void PlaceOrdersIfKickDay()
        {
            if (MainWindow.Instance.ReadEveningPrices(1) == null || MainWindow.Instance.ReadEveningPrices(1).Count == 0)
            {
                this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия дневной торговой сессии или словарь цен не содержит ни одного инструмента.");
            }
            else
            {
                foreach (var security in SecurityList)
                {
                    if (!MainWindow.Instance.ReadEveningPrices(1).ContainsKey(security.Code))
                    {
                        this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия дневной торговой сессии для {0}",
                                        security.Code);
                        continue;
                    }

                    var prevEveningPrice = MainWindow.Instance.ReadEveningPrices(1)[security.Code].Price;
                    this.AddInfoLog("ЦЕНЫ:\nЗакрытие дневной торговой сессии {0} - {1}\n", security.Code,
                                    prevEveningPrice);

                    if (Math.Abs(security.LastTrade.Price - prevEveningPrice) <= prevEveningPrice * _kickPercent / 100)
                        continue;

                    this.AddInfoLog("ВХОД: цена вечернего клиринга - {0}, цена последней сделки - {1}.",
                                    prevEveningPrice, security.LastTrade.Price.ToString(CultureInfo.InvariantCulture));

                    // Если цена не равна нулю
                    if (security.LastTrade.Price != 0)
                    {
                        // Если последняя сделка была не ниже чем вечерний клиринг на соответствующее количество процентов
                        if (security.LastTrade.Price >= prevEveningPrice * (1 + _kickPercent / 100))
                        {
                            // Регистрируем ордер на покупку
                            var kickDayOrder = new Order
                            {
                                Comment = Name + ", enter",
                                Type = OrderTypes.Limit,
                                Volume = SecurityVolumeDictionary[security.Code],
                                Price = security.LastTrade.Price,
                                Portfolio = Portfolio,
                                Security = security,
                                Direction = Sides.Buy
                            };

                            var stopPrice =
                                security.ShrinkPrice(Math.Round((security.LastTrade.Price) * (1 - StopLossPercent / 100)));

                            this.AddInfoLog(
                                "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку 'Ударный день' на {1} по цене {2} c объемом {3} - стоп на {4}",
                                security.Code,
                                kickDayOrder.Direction == Sides.Sell ? "продажу" : "покупку",
                                kickDayOrder.Price.ToString(CultureInfo.InvariantCulture),
                                kickDayOrder.Volume.ToString(CultureInfo.InvariantCulture), stopPrice);

                            RegisterOrder(kickDayOrder);
                        }
                        // Если последняя сделка была не выше чем вечерний клиринг на соответствующее количество процентов
                        else if (security.LastTrade.Price <= prevEveningPrice * (1 - _kickPercent / 100))
                        {
                            var kickDayOrder = new Order
                            {
                                Comment = Name + ", enter",
                                Type = OrderTypes.Limit,
                                Volume = SecurityVolumeDictionary[security.Code],
                                Price = security.LastTrade.Price,
                                Portfolio = Portfolio,
                                Security = security,
                                Direction = Sides.Sell,
                            };

                            var stopPrice =
                                security.ShrinkPrice(Math.Round((security.LastTrade.Price) * (1 + StopLossPercent / 100)));

                            this.AddInfoLog(
                                "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку 'Ударный день' на {1} по цене {2} c объемом {3} - стоп на {4}",
                                security.Code,
                                kickDayOrder.Direction == Sides.Sell ? "продажу" : "покупку",
                                kickDayOrder.Price.ToString(CultureInfo.InvariantCulture),
                                kickDayOrder.Volume.ToString(CultureInfo.InvariantCulture), stopPrice);

                            RegisterOrder(kickDayOrder);
                        }
                    }
                    else
                    {
                        this.AddInfoLog("Цена последней сделки по {0} почему-то равна 0... Игнорируем сигнал на вход.", security.Code);
                    }
                }
            }


            switch (_nextTimeToPlaceOrdersIfKickDay.AddDays(1).DayOfWeek)
            {
                case (DayOfWeek.Saturday):
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(3);
                    break;

                case (DayOfWeek.Sunday):
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(2);
                    break;

                default:
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(1);
                    break;
            }


            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToPlaceOrdersIfKickDay)
                .Do(PlaceOrdersIfKickDay)
                .Once()
                .Apply(this);

            this.AddInfoLog("Следующая попытка: {0}", _nextTimeToPlaceOrdersIfKickDay);
        }
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
namespace SlabReinforcement.Core
{
    // Existing CITRUS lookup, preserved for compatibility; no normative recalculation.
    public static class LegacyAnchorageTable
    {
        public static double GetLength(double barDiameter, string concreteClass, double steelGrade)
        {
            // Данные для анкеровки
            var anchorageLengths = new Dictionary<string, Dictionary<double, Dictionary<double, double>>>
            {
                {
                    "B15", new Dictionary<double, Dictionary<double, double>>
                    {
                        { 240, new Dictionary<double, double>
                            {
                                { 6, 290 }, { 8, 390 }, { 10, 480 }, { 12, 580 }, { 14, 670 },
                                { 16, 770 }, { 18, 860 }, { 20, 960 }, { 22, 1060 }, { 25, 1200 },
                                { 28, 1340 }, { 32, 1530 }, { 36, 1920 } , { 40, 2130 }
                            }
                        },
                        { 400, new Dictionary<double, double>
                            {
                                { 6, 290 }, { 8, 380 }, { 10, 480 }, { 12, 570 }, { 14, 670 },
                                { 16, 760 }, { 18, 860 }, { 20, 950 }, { 22, 1050 }, { 25, 1190 },
                                { 28, 1330 }, { 32, 1520 }, { 36, 1900 }, { 40, 2110 }
                            }
                        },
                        { 500, new Dictionary<double, double>
                            {
                                { 6, 350 }, { 8, 470 }, { 10, 580 }, { 12, 700 }, { 14, 820 },
                                { 16, 930 }, { 18, 1050 }, { 20, 1160 }, { 22, 1280 }, { 25, 1450 },
                                { 28, 1630 }, { 32, 1860 }, { 36, 2320 }, { 40, 2580 }
                            }
                        }
                    }
                },
                {
                    "B20", new Dictionary<double, Dictionary<double, double>>
                    {
                        { 240, new Dictionary<double, double>
                            {
                                { 6, 240 }, { 8, 320 }, { 10, 400 }, { 12, 480 }, { 14, 560 },
                                { 16, 640 }, { 18, 720 }, { 20, 800 }, { 22, 880 }, { 25, 1000 },
                                { 28, 1120 }, { 32, 1280 }, { 36, 1600 }, { 40, 1770 }
                            }
                        },
                        { 400, new Dictionary<double, double>
                            {
                                { 6, 240 }, { 8, 320 }, { 10, 400 }, { 12, 480 }, { 14, 560 },
                                { 16, 640 }, { 18, 710 }, { 20, 790 }, { 22, 870 }, { 25, 990 },
                                { 28, 1110 }, { 32, 1270}, { 36, 1580 }, { 40, 1760 }
                            }
                        },
                        { 500, new Dictionary<double, double>
                            {
                                { 6, 290 }, { 8, 390 }, { 10, 490 }, { 12, 580 }, { 14, 680 },
                                { 16, 780 }, { 18, 870 }, { 20, 970 }, { 22, 1070 }, { 25, 1210 },
                                { 28, 1360 }, { 32, 1550 }, { 36, 1940 }, { 40, 2150 }
                            }
                        }
                    }
                },
                {
                    "B25", new Dictionary<double, Dictionary<double, double>>
                    {
                        { 240, new Dictionary<double, double>
                            {
                                { 6, 210 }, { 8, 280 }, { 10, 350 }, { 12, 410 }, { 14, 480 },
                                { 16, 550 }, { 18, 620 }, { 20, 690 }, { 22, 750 }, { 25, 860 },
                                { 28, 960 }, { 32, 1100 }, { 36, 1370 }, { 40, 1520 }
                            }
                        },
                        { 400, new Dictionary<double, double>
                            {
                                { 6, 210 }, { 8, 270 }, { 10, 340 }, { 12, 410 }, { 14, 480 },
                                { 16, 540 }, { 18, 610 }, { 20, 680 }, { 22, 750 }, { 25, 850 },
                                { 28, 950 }, { 32, 1090 }, { 36, 1360 }, { 40, 1510 }
                            }
                        },
                        { 500, new Dictionary<double, double>
                            {
                                { 6, 250 }, { 8, 340 }, { 10, 420 }, { 12, 500 }, { 14, 580 },
                                { 16, 670 }, { 18, 750 }, { 20, 830 }, { 22, 920 }, { 25, 1040 },
                                { 28, 1160 }, { 32, 1330 }, { 36, 1660 }, { 40, 1850 }
                            }
                        }
                    }
                },
                {
                    "B30", new Dictionary<double, Dictionary<double, double>>
                    {
                        { 240, new Dictionary<double, double>
                            {
                                { 6, 200 }, { 8, 250 }, { 10, 320 }, { 12, 380 }, { 14, 440 },
                                { 16, 500 }, { 18, 560 }, { 20, 630 }, { 22, 690 }, { 25, 780 },
                                { 28, 880 }, { 32, 1000 }, { 36, 1250 }, { 40, 1390 }
                            }
                        },
                        { 400, new Dictionary<double, double>
                            {
                                { 6, 200 }, { 8, 250 }, { 10, 310 }, { 12, 370 }, { 14, 440 },
                                { 16, 480 }, { 18, 560 }, { 20, 620 }, { 22, 680 }, { 25, 780 },
                                { 28, 870 }, { 32, 990 }, { 36, 1240 }, { 40, 1380 }
                            }
                        },
                        { 500, new Dictionary<double, double>
                            {
                                { 6, 230 }, { 8, 310 }, { 10, 380 }, { 12, 460 }, { 14, 530 },
                                { 16, 610 }, { 18, 680 }, { 20, 760 }, { 22, 840 }, { 25, 950 },
                                { 28, 1060 }, { 30, 1210 }, { 32, 1520 }, { 36, 1690 }, { 40, 1690 }
                            }
                        }
                    }
                }
            };

            // Проверяем наличие данных для указанного класса бетона
            if (!anchorageLengths.ContainsKey(concreteClass))
                throw new ArgumentException($"Для бетона {concreteClass} нужен отдельный расчёт анкеровки.");

            var steelData = anchorageLengths[concreteClass];

            // Проверяем наличие данных для указанной марки стали
            if (!steelData.ContainsKey(steelGrade))
            {
                // Поиск ближайшего значения для steelGrade
                double closestSteelGrade = steelData.Keys
                    .OrderBy(key => Math.Abs(key - steelGrade))
                    .FirstOrDefault();

                if (Math.Abs(closestSteelGrade - steelGrade) < 0.0001) // Допустимая точность
                {
                    steelGrade = closestSteelGrade;
                }
                else
                {
                    throw new ArgumentException($"Не найдена анкеровка для {steelGrade}. Проверьте класс стали типа.");
                }
            }

            var diameterData = steelData[steelGrade];

            // Проверяем наличие данных для указанного диаметра
            if (!diameterData.ContainsKey(barDiameter))
            {
                // Поиск ближайшего значения для barDiameter
                double closestBarDiameter = diameterData.Keys
                    .OrderBy(key => Math.Abs(key - barDiameter))
                    .FirstOrDefault();

                if (Math.Abs(closestBarDiameter - barDiameter) < 0.0001) // Допустимая точность
                {
                    barDiameter = closestBarDiameter;
                }
                else
                {
                    throw new ArgumentException($"Для диаметра {barDiameter} нужен отдельный расчёт анкеровки.");
                }
            }

            // Возвращаем длину анкеровки
            return diameterData[barDiameter];
        }
    }
}

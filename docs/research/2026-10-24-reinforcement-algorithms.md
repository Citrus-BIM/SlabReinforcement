# Исследование алгоритмов для SlabReinforcement

Дата: 24 октября 2026. Исследованы исходники открытых проектов, существующие тесты и примеры. Часть геометрических алгоритмов выполнена локально в отдельных временных каталогах. В Revit и FreeCAD сторонние плагины не запускались.

## Требование пользователя

Приоритет — надёжный алгоритм построения зон, учитывающий поворот плиты и CAD-подложки. Основное направление задаёт пользователь по выбранному ребру плиты. Требуется удобный WPF-интерфейс в существующем стиле CITRUS.

**Рекомендация по результатам исследования:** собственное тестируемое ядро обработки цветных `3DFACE`, локальная система координат от выбранного ребра, NetTopologySuite для контуров и отверстий, интервальное построение рядов с проверкой результата после объединения. OpenRebar полезен как источник конкретных алгоритмов и тестовых сценариев, но целиком готовой заменой текущему модулю не является.

## Что проверено исполнением

| Источник | Проверка | Результат |
|---|---|---|
| OpenRebar `e08d59f` | Выбранные тесты DXF, интервалов, контуров, отверстий, полей As и наложения зон; net8.0 Release, locked restore | 38 + 26 = **64 пройдено**, 0 пропущено. Это выбранная часть набора, не все тесты репозитория |
| OpenRebar `e08d59f` | Дополнительный случай: прямоугольная плита с наклонным отверстием, Ø12/200 | **Дефект воспроизведён**: точные интервалы корректны, после объединения 8 рядов заходят в отверстие до 160 мм; после отдельного вызова стандартного калькулятора остаются 8 пересечений до 124 мм |
| NetTopologySuite 2.6.0 + Clipper2, tag `Clipper2_2.0.1` | Прямоугольная область с отверстием: повороты 0°, 17°, 45°, 89°, каждый с отражением и без, перенос на большие координаты | **16 проверок пройдено**: по восемь на библиотеку; линия разбивается на два отрезка, их суммарная длина сохраняется |
| NetTopologySuite 2.6.0 | Г-образный контур и сужение рабочей области с сохранением отверстия | **2 проверки пройдено** |
| Clipper2, tag `Clipper2_2.0.1`, commit `21ebba05db8894f0c7217ad35ea518080f324946` | Штатный C# проект `Tests1`, Release | **4 из 5 тестовых методов пройдены**. `TestClosedPaths` падает на наборе №16: ожидается 2 полигона, получен 1; ожидаемая площадь 376, получена 396 |
| Intelligent-Rebar-Detailing `5c263ef` | Низкоуровневое вычитание центрального, краевого, пересекающихся и полностью перекрывающего отверстия, оба направления | **8 проверок пройдено** |
| Тот же проект | Полный `rectangle_subtraction2`, сквозная прорезь шириной 5 при допуске объединения 10 | **Дефект воспроизведён**: отверстие исчезает после объединения |
| Тот же проект | Ограничение длины 12000, участок 24000, нахлёст 40 | **Дефект воспроизведён**: итоговые отрезки по 12040 |

Для диагностики Clipper2 в отдельной временной копии добавлено только сообщение к существующему assert; алгоритм и критерий сравнения не изменялись. Причина несовпадения эталона и результата не установлена. Успех отдельных проверок не отменяет падение штатного набора. Библиотека остаётся альтернативой, а не выбранной зависимостью проекта.

Эти проверки подтверждают отдельные геометрические операции. Они не подтверждают работу всей цепочки DXF → Revit и нормативную правильность расчёта анкеровки.

## 1. OpenRebar — ближайший предметный аналог

Репозиторий: [KonkovDV/OpenRebar](https://github.com/KonkovDV/OpenRebar). Исследован commit [`e08d59f1f709dade32ad205f7b574c44de16ca37`](https://github.com/KonkovDV/OpenRebar/commit/e08d59f1f709dade32ad205f7b574c44de16ca37). [Лицензия MIT](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/LICENSE).

### Полезные алгоритмы

- [`BarRunBuilder.IntervalsOnLine`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/ReinforcementEngine/BarRunBuilder.cs#L128): пересекает горизонтальную/вертикальную линию со сторонами полигона, сортирует точки пересечения, образует попарные интервалы, объединяет интервалы внешних оболочек и вычитает интервалы отверстий. Полуоткрытое условие пересечения сторон предотвращает двойной учёт вершины. Это подходящая основа для построения рядов в локальной системе плиты.
- [`BarRunBuilder.Build`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/ReinforcementEngine/BarRunBuilder.cs#L15): расставляет линии по шагу, строит интервалы на каждой линии, объединяет соседние строки с близкими концами. Длина группы расширяется наружу. **После такого расширения в нашем модуле необходима повторная проверка каждой конечной линии относительно контура и отверстий.**
- [`WorkingAreaBuilder.Build`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/Geometry/WorkingAreaBuilder.cs#L12): формирует область допустимого расположения осей стержней через отступ от свободных краёв, расширение отверстий и разность полигонов. Ветка объявленных опор использует отдельную прямоугольную модель; её нельзя автоматически переносить на произвольную плиту.
- [`LayerClassOverlay.Apply`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Application/UseCases/LayerClassOverlay.cs#L15): разбивает перекрывающиеся зоны одного слоя/направления на непересекающиеся участки. В пересечении сохраняет класс с большим As. Полезно для исключения дублей и потери более высокого требования.
- [`NtsPlanarGeometry`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/Geometry/NtsPlanarGeometry.cs#L17): boolean-операции, buffer, polygonize, spatial index, обработка внутренних колец. Использует координатную сетку 0,1 мм. Этот допуск является выбором проекта, а не универсальной нормой для CITRUS.

### Почему нельзя перенести проект целиком

[`DxfIsolineParser`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/DxfProcessing/DxfIsolineParser.cs#L52) читает `LWPOLYLINE`, `POLYLINE`, `HATCH`, `INSERT`, учитывает цвета, дуговые bulge и единицы. Обработки `3DFACE` нет. Наш модуль использует именно цветные грани конечных элементов.

[`TabularAsFieldReader`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Application/UseCases/TabularAsFieldReader.cs#L57) принимает прямоугольные ячейки minX/minY/maxX/maxY. [`AsFieldZoneBuilder`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Application/UseCases/AsFieldZoneBuilder.cs#L19) объединяет группу, когда её объединение является прямоугольным; иначе оставляет отдельные ячейки. Это не готовый алгоритм зон по произвольной треугольной сетке.

Revit-адаптер ориентирован на мировые XY. Код [`RevitRebarPlacer`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.RevitPlugin/Revit/RevitRebarPlacer.cs#L3) находится под `REVIT_SDK`; обычный [csproj](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.RevitPlugin/OpenRebar.RevitPlugin.csproj#L26) не подключает Revit API. [Успешный CI](https://github.com/KonkovDV/OpenRebar/actions/runs/39434544504) не доказывает работоспособность этой ветки. Само ядро на net8/net10 также нельзя напрямую подключить к нашим конфигурациям net48.

### Проверенный контрпример к объединению рядов

Плита 3000×1400 мм, отверстие с вершинами `(1000,200), (1400,200), (1600,1200), (1200,1200)`, горизонтальная арматура Ø12/200. При переходе на следующий ряд конец интервала перемещается на 40 мм.

`IntervalsOnLine` возвращает правильные интервалы. Например, на y=300 левый отрезок `[0;1020]` заканчивается у отверстия. `Build` расширяет его до `[0;1180]`: **160 мм оказываются внутри отверстия**. Всего восемь рядов имеют пересечения.

Причина в двух местах: [`Build:24–25`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/ReinforcementEngine/BarRunBuilder.cs#L24) учитывает наклонность только внешней границы, а [`OpenRun.Extend:320–325`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Infrastructure/ReinforcementEngine/BarRunBuilder.cs#L320) расширяет общие пределы min/max. Последовательные изменения по 40 мм укладываются в допуск 50 мм, но накапливаются в группе.

Отдельный вызов `StandardReinforcementCalculator` с обычными крюками не устраняет дефект: восемь конечных стержней пересекают отверстие, максимум на 124 мм. Если отверстие задано только в `zone.Holes`, [`ClearanceChecker`](https://github.com/KonkovDV/OpenRebar/blob/e08d59f1f709dade32ad205f7b574c44de16ca37/src/OpenRebar.Application/UseCases/ClearanceChecker.cs#L40) не сообщает коллизий. При дублировании в `slab.Openings` сообщает восемь `barInOpening`, но не изменяет стержни. Проверка покрытия As в обоих случаях возвращает Passed: она решает другую задачу и не заменяет геометрическую проверку. Требуемое As в опыте равно номинальному Ø12/200, 565,4867 мм²/м; шаг проверочной сетки — 200 мм.

Это проверка отдельных стадий, не запуск всего приложения. **Вывод: заимствовать принцип точного интервального отсечения; `Build` не считать готовым алгоритмом безопасного объединения.** Существующие 64 успешных теста не покрывали этот случай.

## 2. NetTopologySuite — основа операций над контурами

Репозиторий: [NetTopologySuite/NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite). Проверенная версия [2.6.0](https://github.com/NetTopologySuite/NetTopologySuite/releases/tag/v2.6.0). [Файл лицензий](https://github.com/NetTopologySuite/NetTopologySuite/blob/v2.6.0/License.md) описывает BSD-условия проекта, происхождение от JTS и сторонние компоненты.

Это геометрическая библиотека, а не расчётчик армирования. Полезны Polygon/MultiPolygon с внутренними кольцами, union/intersection/difference, buffer, polygonizer и STRtree.

[`OverlayNGRobust.Overlay`](https://github.com/NetTopologySuite/NetTopologySuite/blob/v2.6.0/src/NetTopologySuite/Operation/OverlayNG/OverlayNGRobust.cs) сначала выполняет обычное пересечение с проверкой корректности, при сбое пробует snapping с увеличением допуска, затем snap-rounding. Неустранимый сбой возвращается исключением. Для инженерного ядра изменение геометрии из-за допуска нужно контролировать по площади и сохранности отверстий.

[`MinimumAreaRectangle`](https://github.com/NetTopologySuite/NetTopologySuite/blob/v2.6.0/src/NetTopologySuite/Algorithm/MinimumAreaRectangle.cs) использует выпуклую оболочку и rotating calipers; есть [регрессионные тесты](https://github.com/NetTopologySuite/NetTopologySuite/blob/v2.6.0/test/NetTopologySuite.Tests.NUnit/Algorithm/MinimumAreaRectangleTest.cs). Для нашей задачи это только возможная подсказка ориентации: выбранное пользователем ребро имеет приоритет, и охватывающий прямоугольник не заменяет реальный контур.

## 3. DynamoForRebar — построение кривых отдельно от Revit

[tt-acm/DynamoForRebar](https://github.com/tt-acm/DynamoForRebar), commit `48d2b9d40dfa00aa79f470799de58047396bf0ef`, [Apache-2.0](https://github.com/tt-acm/DynamoForRebar/blob/48d2b9d40dfa00aa79f470799de58047396bf0ef/LICENSE).

[`Rebar.ByCurve`](https://github.com/tt-acm/DynamoForRebar/blob/48d2b9d40dfa00aa79f470799de58047396bf0ef/src/DynamoRebar/Revit/Rebar.cs#L310-L350) принимает готовую кривую и нормаль, отдельно выполняет создание/обновление Revit-арматуры. Это полезное разделение ответственности.

[`SurfaceExtensions.Follow`](https://github.com/tt-acm/DynamoForRebar/blob/48d2b9d40dfa00aa79f470799de58047396bf0ef/src/DynamoRebar/Extensions/SurfaceExtensions.cs#L140-L185) строит линии постоянного U/V на поверхности. [`TrimmedSurface.GetCurveAtParameter`](https://github.com/tt-acm/DynamoForRebar/blob/48d2b9d40dfa00aa79f470799de58047396bf0ef/src/DynamoRebar/TrimmedSurface.cs#L152-L185) соединяет точки противоположных границ. Этот способ не является универсальным отсечением по нескольким отверстиям.

Есть [RVT/RFA/DYN-примеры](https://github.com/tt-acm/DynamoForRebar/tree/48d2b9d40dfa00aa79f470799de58047396bf0ef/TestFiles) и [тесты графов](https://github.com/tt-acm/DynamoForRebar/blob/48d2b9d40dfa00aa79f470799de58047396bf0ef/src/DynamoRebarTest/Class1.cs#L91-L160). Исследованная версия ориентирована на Revit 2019/Dynamo 2; тесты здесь не запускались.

## 4. Revit SDK — контракт создания зон и направление от ребра

[jeremytammik/RevitSdkSamples](https://github.com/jeremytammik/RevitSdkSamples), commit `4ca6a3d6f0ae6e166b514e8324f65f057da27b64`.

[`CreateComplexAreaRein`](https://github.com/jeremytammik/RevitSdkSamples/blob/4ca6a3d6f0ae6e166b514e8324f65f057da27b64/SDK/Samples/CreateComplexAreaRein/CS/CreateComplexAreaRein.cs#L85-L101) получает majorDirection как разность концов прямого ребра и передаёт его в `AreaReinforcement.Create`. В нашем сценарии направление следует брать с выбранного ребра.

Пример ограничен прямоугольной горизонтальной плитой. [`AddInlaidCurves`](https://github.com/jeremytammik/RevitSdkSamples/blob/4ca6a3d6f0ae6e166b514e8324f65f057da27b64/SDK/Samples/CreateComplexAreaRein/CS/GeomHelper.cs#L109-L145) создаёт искусственный внутренний прямоугольник, а не извлекает произвольные отверстия. В `NewPathReinforcement` преобразования привязаны к виду с именем `Level 2`. Примеры подходят для изучения API, но не для переноса алгоритма целиком.

Лицензирование SDK определяется также заголовками Autodesk и [SDK EULA](https://github.com/jeremytammik/RevitSdkSamples/blob/4ca6a3d6f0ae6e166b514e8324f65f057da27b64/SDK/RevitAPISDK_Eula.rtf), а не только MIT-файлом корня зеркала.

## 5. FreeCAD Reinforcement — слои и наборы стержней

[amrit3701/FreeCAD-Reinforcement](https://github.com/amrit3701/FreeCAD-Reinforcement), commit `9f424aca0b0ada2521fab3927108af4e23eccdc0`. По [package.xml](https://github.com/amrit3701/FreeCAD-Reinforcement/blob/9f424aca0b0ada2521fab3927108af4e23eccdc0/package.xml) — LGPL-2.1-or-later; старые заголовки некоторых файлов указывают LGPL version 2 or later.

Реально реализованы параллельный и поперечный наборы, защитные слои, разные формы стержней и высотный сдвиг второго направления на диаметр первого. См. [`SlabReinforcementObject`](https://github.com/amrit3701/FreeCAD-Reinforcement/blob/9f424aca0b0ada2521fab3927108af4e23eccdc0/SlabReinforcement/SlabReinforcementObject.py#L942-L990) и [`StraightRebar.makeStraightRebar`](https://github.com/amrit3701/FreeCAD-Reinforcement/blob/9f424aca0b0ada2521fab3927108af4e23eccdc0/StraightRebar.py#L388-L443).

Поворот частично учитывается через Placement и привязку эскиза к грани. Однако [`getParametersOfFace`](https://github.com/amrit3701/FreeCAD-Reinforcement/blob/9f424aca0b0ada2521fab3927108af4e23eccdc0/Rebarfunc.py#L183-L271) использует две длины, центр и bounding box; изученная цепочка не вырезает произвольные отверстия из каждого стержня. Полезна модель слоёв, но это не готовый геометрический движок для нашей задачи.

## 6. Другие рассмотренные проекты

### Intelligent-Rebar-Detailing

[Jon-hattan/Intelligent-Rebar-Detailing](https://github.com/Jon-hattan/Intelligent-Rebar-Detailing), commit `5c263ef27845ceea319f9a055cdf604e94932249`. Реальный прототип обработки планов и аннотирования PDF.

[`rectangle_subtraction2`](https://github.com/Jon-hattan/Intelligent-Rebar-Detailing/blob/5c263ef27845ceea319f9a055cdf604e94932249/Preprocessors/Rectangle_subtraction.py#L95-L155) режет прямоугольник по границам отверстий и вычитает интервалы. [`Box_grouper2`](https://github.com/Jon-hattan/Intelligent-Rebar-Detailing/blob/5c263ef27845ceea319f9a055cdf604e94932249/Processor/Box_grouper2.py#L194-L259) объединяет полосы. [`optimal_lines`](https://github.com/Jon-hattan/Intelligent-Rebar-Detailing/blob/5c263ef27845ceea319f9a055cdf604e94932249/Processor/optimal_lines.py#L40-L102) применяет динамическое программирование: выбирает цепочку допустимых сегментов, максимизируя минимальную длину в цепочке.

Обнаруженные исполнением ошибки приведены выше. Геометрия осевая, допуски пиксельные; лицензионный файл в исследованном дереве не найден. Полезны идеи интервального вычитания и графа стыков, но копировать этот pipeline как рабочее ядро нельзя.

### FEM-Design API

[ms-structural/femdesign-api](https://github.com/ms-structural/femdesign-api), commit `ad9b03504d2379f4e374ab89b23b19c436081fc2`. [`SurfaceReinforcementParameters.Straight`](https://github.com/ms-structural/femdesign-api/blob/ad9b03504d2379f4e374ab89b23b19c436081fc2/FemDesign.Core/Reinforcement/SurfaceReinforcementParameters.cs#L57) задаёт направления в локальных осях плиты. Полезна модель «область + слой + направление + параметры». Вычисления выполняются внешним FEM-Design: открытый репозиторий является API-обёрткой, а не открытым решателем армирования. Core содержит MIT-лицензию.

### thiruAji/revit-rebar-automation

Исследован commit `d7d52f7bfd335dc9d6df20271651b4983ba0a065`. В [`RebarPlacer`](https://github.com/thiruAji/revit-rebar-automation/blob/d7d52f7bfd335dc9d6df20271651b4983ba0a065/RevitIntegration/RebarPlacer.cs#L72) вычисленный cover не используется, верхнее и нижнее армирование получают одну исходную отметку, есть несоответствие направлений и длин. [Workflow](https://github.com/thiruAji/revit-rebar-automation/blob/d7d52f7bfd335dc9d6df20271651b4983ba0a065/.github/workflows/build.yml#L24) создаёт искусственные Revit API DLL; [последняя проверенная сборка падает](https://github.com/thiruAji/revit-rebar-automation/actions/runs/21793103085). Не выбран как основа.

## Предлагаемая схема алгоритма CITRUS

Это вывод исследования и предложение для дальнейшего проектирования; схема ещё не реализована.

1. **Плоскость и оси плиты.** Пользователь выбирает плиту и прямое ребро для X. Нормаль выбранной плоской грани задаёт n; направление ребра проецируется на эту плоскость и нормируется: `x = normalize(e - n*dot(e,n))`, `y = n × x`. Начало рядом с плитой уменьшает влияние больших координат. Низ/верх определяются относительно плиты. Для дугового ребра нужен явно выбранный способ задания направления; молчаливое принятие хорды недопустимо.
2. **Преобразование подложки.** Входные DXF-координаты, единицы и размещение CAD переводятся в координаты Revit ровно один раз, затем — в локальную систему плиты. Проверить реальные случаи м/мм, масштаб, отражение, true north и смещение. Autodesk описывает [`GetTotalTransform`](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/8c8aff2b-5ff9-e43a-3b5c-308cd0174f1f.htm) как включающий true north у ImportInstance. Само имя метода не доказывает правильность всей цепочки единиц исходного DXF. Оси армирования и размещение подложки — разные входные данные.
3. **Контуры.** Получить внешний контур и все внутренние кольца из геометрии плиты, привести к одной плоскости, аппроксимировать дуги с контролируемой погрешностью. Определять оболочки и отверстия по вложенности, не по максимальному периметру и не через bbox. Не сводить все исходные отметки к Z=0 до проверки их принадлежности выбранной плите.
4. **Поле армирования.** Читать треугольные/четырёхугольные `3DFACE`, исправлять только объяснимые дубли, сохранять RGB и назначенные тип/шаг. Строить полигональные области по совместимым параметрам. Перекрытия одного слоя разбирать явно. Сравнение по As допустимо лишь при совместимом материале/классе и смысле исходных данных. Поворот координат не пересчитывает компоненты расчётного армирования.
5. **Рабочая область.** Вычесть отверстия и защитные отступы, сохраняя раздельные островки. Проверять изменение площади, исчезновение узких областей и топологию после snapping/упрощения.
6. **Ряды и группы.** Пересекать линии заданного шага с реальной областью. Отверстие создаёт разрыв интервала. Объединять только совместимые соседние строки; после расширения группы заново проверять каждую её линию. Запретить объединение через отверстие даже при расстоянии меньше допуска.
7. **Анкеровка, округление и длины.** Сначала определить требуемое продолжение за расчётную область, затем проверить доступное место в плите. Недостаток места — отдельный результат, а не молчаливое обрезание анкеровки. После округления и нахлёстов повторно проверять конечную длину и геометрию. Таблицы анкеровки текущего проекта требуют отдельной проверки; найденные репозитории их корректность не подтверждают.
8. **План результата.** Ядро возвращает геометрию, параметры, привязку к исходным граням и диагностические результаты без Revit-объектов. Один этот план используется предпросмотром и построением. Для одинаковых прямых рядов подходят групповые системы; ограничения `PathReinforcement`/`AreaReinforcement` на реальных сложных контурах проверяются отдельно. Не подменять геометрию прямоугольником ради API.
9. **Revit-адаптер.** Только чтение/создание элементов и транзакции. Считать фактически созданные/пропущенные зоны, показывать причины, проверять параметры и итог commit. Не обращаться к Revit API из фоновых расчётов.

Основание для собственного базиса: Autodesk предупреждает, что произведение [`PlanarFace.XVector × YVector`](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/05128277-2f8d-79ad-0e93-8a1842fcc2a4.htm) может быть противоположно FaceNormal. Заводские UV-направления также не обязаны совпадать с выбранным ребром.

## Варианты внедрения

| Вариант | Преимущество | Ограничение |
|---|---|---|
| Подключить существующий ZoneGeometryEngine и исправить поворот | Наименьший объём начальных изменений | Bounding-box группировка и неполная обработка отверстий сохранятся; это промежуточный шаг |
| **Собственное ядро + NTS + проверенные идеи OpenRebar** | Сохраняет вход `3DFACE`, версии Revit и стиль CITRUS; геометрия проверяется вне Revit | Нужны собственные тесты топологии, поворотов, анкеровки и окончательных зон |
| Перенести OpenRebar целиком | Много готовых подсистем | Иной формат данных, net8/net10, незавершённая Revit-интеграция; большой объём адаптации |

Рекомендуется второй вариант. Геометрические библиотеки подключать через небольшой интерфейс, без переноса всех сторонних зависимостей приложения.

## Обязательные проверки нового ядра

- Повороты 0/17/45/89/90 градусов, перенос, отражение CAD, перестановка концов выбранного ребра. После обратного преобразования геометрия и количество стержней должны соответствовать исходному случаю в пределах заданного допуска.
- Независимые повороты подложки и плиты, исходные единицы м/мм, большой сдвиг координат.
- Прямоугольник, Г- и П-образные контуры, несколько отверстий, наклонные стороны отверстий, узкая прорезь меньше допуска объединения, несколько раздельных островков.
- Треугольная и четырёхугольная сетки, разные порядки вершин/граней, дубли, малые зазоры, разные отметки.
- Никакая конечная линия после группировки, анкеровки и округления не пересекает отверстие и не выходит за допустимую область. Недостаточная анкеровка не выдаётся за успешное построение.
- Требуемое армирование выбранного слоя не уменьшается при объединении; более высокий класс не теряется в пересечениях. Все пропуски имеют объяснение.
- Предпросмотр и построение используют одинаковый план; отмена не изменяет модель, а ошибки создания не скрываются.

## Интерфейс в стиле проекта

Основа уже есть в `SlabReinforcement/CitrusTheme`: светлые поверхности, акцент `#FE8841`, Segoe UI, общие кнопки, поля и таблицы. Предлагается подключить эти ресурсы и построить интерфейс вокруг геометрического предпросмотра: выбранная плита и ребро, видимые стрелки X/Y, совмещение CAD, контуры и отверстия, таблица «цвет → тип/шаг → ожидаемые зоны», выделение проблемных участков. Изменение параметров обновляет план, создание запускается отдельным явным действием. Сводка показывает фактический результат и конкретные причины пропуска.

## Артефакты проверки

Временные копии сторонних проектов и проверочные программы находятся вне рабочего репозитория:

- OpenRebar: `C:\Users\Misha\AppData\Local\Temp\codex-openrebar-review-21c2bae6c0654fb9ae874a1f3eac06f6`. Результаты: `tests/OpenRebar.Infrastructure.Tests/TestResults/research-geometry.trx` и `tests/OpenRebar.Application.Tests/TestResults/research-field.trx`.
- Контрпример OpenRebar с наклонным отверстием: `C:\Users\Misha\AppData\Local\Temp\codex-openrebar-holeprobe-20261024`, `Program.cs` и `result.json`.
- Clipper2 и собственные 18 проверок библиотек: `C:\Users\Misha\AppData\Local\Temp\slab-algorithm-research-20261024`. Программа `GeometryProbe/Program.cs`, проект `GeometryProbe/GeometryProbe.csproj`.
- Проверки Intelligent-Rebar-Detailing: `C:\Users\Misha\AppData\Local\Temp\codex-slab-research-20261024`, файлы `probe_geometry.py`, `probe_length.py` и соответствующие `*_output.txt`.

Исходники плагина в рамках этого исследования не менялись. Этот документ фиксирует исследование и предлагаемое направление; он не является отчётом о завершённой модернизации.

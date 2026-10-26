# Arduino Studio 7

**Алтернатива на Arduino IDE за Windows 7** — C# Windows Forms среда с интерфейс в стил Visual Studio.

> Статус: начален работещ скелет на проекта. Подсказките в тази версия се извличат локално от кода; това не е пълен семантичен IntelliSense. Компилацията и качването се изпълняват чрез външния Arduino CLI.

## Възможности в първата версия
- Интерфейс на български, менюта, панел „Проект“, редактор и прозорец за изход.
- Отваряне и записване на .ino, .h, .hpp, .c и .cpp файлове.
- Оцветяване на C/C++ ключови думи, коментари, низове и числа.
- Подсказки при задържане на мишката върху функции, типове, променливи и дефиниции, извлечени от отворения код.
- Автоматично допълване на често използвани Arduino думи и символи от текущия файл.
- Избор на FQBN на платката и сериен COM порт.
- Компилация и качване чрез Arduino CLI; платките зависят от инсталираните core пакети и драйвери.
- GitHub Actions workflow за компилация на Windows приложение и публикуване на ZIP артефакт.

## Изисквания
- Windows 7 SP1, 32- или 64-битов; препоръчват се наличните обновления.
- .NET Framework 4.7.2 Runtime.
- За Arduino: съвместим arduino-cli.exe, инсталирани board core пакети и USB драйвер.
- GitHub Actions компилира приложението на Windows runner; това не гарантира съвместимост на всеки външен board core с Windows 7.

## Стартиране от изходния код
1. Инсталирайте Visual Studio 2019 или 2022 с .NET desktop development.
2. Отворете Arduino-Studio-7.sln.
3. Изберете Release и компилирайте.
4. Стартирайте ArduinoStudio7.exe.
5. В меню Инструменти → Настройки на Arduino CLI... задайте пътя към arduino-cli.exe, ако не е в PATH.

## Настройка на Arduino CLI
Изтеглете Arduino CLI от https://arduino.github.io/arduino-cli/

Примерни команди:

    arduino-cli core update-index
    arduino-cli core install arduino:avr
    arduino-cli board list

За Arduino Mega 2560 FQBN е arduino:avr:mega; за Uno — arduino:avr:uno. За ESP32, SAMD и други семейства инсталирайте съответния core според официалните инструкции на производителя.

## Забележки за Windows 7
Новите версии на Arduino CLI и някои производителски core пакети може да изискват по-нова версия на Windows. Ако инструментът не стартира, използвайте версия на CLI и core пакет, които изрично работят на вашата система. Програмата не включва чужди компилатори автоматично.

## Структура
- src/ArduinoStudio7/ — C# Windows Forms приложение.
- .github/workflows/build.yml — автоматична компилация в GitHub Actions.

## Лиценз
MIT. Вижте LICENSE.
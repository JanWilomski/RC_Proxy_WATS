# RC_Proxy_WATS

RC_Proxy_WATS to aplikacja proxy, która działa jako pośrednik między serwerem RC a klientami RC GUI. Głównym celem jest optymalizacja żądań `rewind` poprzez przechowywanie wiadomości CCG w RabbitMQ i serwowanie ich z cache'u zamiast przekazywania żądań do serwera RC.

## Funkcjonalności

- **Transparentny proxy**: Przekazuje wszystkie wiadomości między RC serwerem a klientami RC GUI
- **Cache wiadomości CCG**: Automatycznie zapisuje wszystkie wiadomości CCG do RabbitMQ
- **Optymalizacja rewind**: Obsługuje żądania rewind z lokalnego cache'u zamiast obciążania serwera RC
- **Wieloklienci**: Obsługuje jednoczesne połączenia wielu klientów RC GUI
- **Automatyczne zarządzanie**: TTL i limity rozmiaru kolejki w RabbitMQ
- **Monitoring**: Szczegółowe logowanie i statystyki

## Architektura

```
RC GUI Client 1 ──┐
RC GUI Client 2 ──┼─── RC_Proxy_WATS ───── RC Server
RC GUI Client N ──┘         │
                             │
                         RabbitMQ
                      (CCG Messages)
```

## Wymagania

- .NET 8.0
- RabbitMQ Server
- Dostęp do serwera RC

## Instalacja

1. **Zainstaluj RabbitMQ**:
   ```bash
   # Ubuntu/Debian
   sudo apt-get install rabbitmq-server
   
   # Windows - pobierz z https://www.rabbitmq.com/download.html
   
   # Docker
   docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3-management
   ```

2. **Sklonuj i zbuduj projekt**:
   ```bash
   git clone <repository-url>
   cd RC_Proxy_WATS
   dotnet restore
   dotnet build
   ```

## Konfiguracja

Edytuj plik `appsettings.json`:

```json
{
  "ProxyConfiguration": {
    "RcServerHost": "127.0.0.1",           // Adres serwera RC
    "RcServerPort": 19083,                 // Port serwera RC
    "ProxyListenPort": 19084,              // Port na którym proxy nasłuchuje
    "ProxyBindAddress": "0.0.0.0",         // Adres bind proxy
    "MaxConcurrentClients": 100,           // Maksymalna liczba klientów
    "HeartbeatIntervalSeconds": 30,        // Interwał heartbeat
    "ConnectionTimeoutSeconds": 60,        // Timeout połączeń
    "InitializationTimeoutMinutes": 5,     // Timeout inicjalizacji z rewind
    "EnableDebugLogging": true             // Debug logging
  },
  "RabbitMQ": {
    "HostName": "localhost",               // Adres RabbitMQ
    "Port": 5672,                         // Port RabbitMQ
    "UserName": "guest",                  // Użytkownik RabbitMQ
    "Password": "guest",                  // Hasło RabbitMQ
    "VirtualHost": "/",                   // Virtual host
    "CcgMessagesQueueName": "ccg_messages", // Nazwa kolejki
    "MaxMessagesInQueue": 50000,          // Maksymalna liczba wiadomości
    "MessageTtlHours": 24                 // TTL wiadomości w godzinach
  }
}
```

## Uruchomienie

### Sekwencja uruchomienia

1. **Uruchom RabbitMQ**:
   ```bash
   # Linux
   sudo systemctl start rabbitmq-server
   
   # Windows Service Manager lub
   rabbitmq-server
   
   # Docker
   docker start rabbitmq
   ```

2. **Upewnij się, że RC Server jest uruchomiony** (na porcie 19083)

3. **Uruchom RC_Proxy_WATS**:
   ```bash
   dotnet run
   ```

   **Ważne**: Proxy wykona inicjalizację z rewind, która może zająć kilka minut:
   ```
   [INFO] Sent rewind request to RC server, waiting for historical data...
   [INFO] Initialization progress: 500 historical CCG messages processed
   [INFO] RC server initialization completed - loaded 1234 historical CCG messages
   [INFO] RC_Proxy_WATS service started successfully and ready for client connections
   ```

4. **Skonfiguruj klientów RC GUI**:
   Zmień konfigurację w klientach RC GUI, aby łączyły się z proxy zamiast bezpośrednio z RC:
   ```
   ServerIP: 127.0.0.1 (lub adres serwera proxy)
   ServerPort: 19084 (lub port skonfigurowany w proxy)
   ```

5. **Uruchom klientów RC GUI** - powinni się łączyć bez problemów

## Jak to działa

1. **Inicjalizacja**:
    - Proxy łączy się z RabbitMQ
    - Ładuje istniejące wiadomości CCG z RabbitMQ do cache'u
    - Łączy się z serwerem RC
    - **Wysyła żądanie rewind do RC** aby pobrać wszystkie historyczne wiadomości CCG
    - Czeka na wiadomość "rewind complete" od RC serwera
    - Dopiero po zakończeniu inicjalizacji rozpoczyna nasłuchiwanie połączeń od klientów

2. **Normalna operacja**:
    - Wszystkie wiadomości od klientów RC GUI są przekazywane do serwera RC
    - Wszystkie wiadomości od serwera RC są przekazywane do klientów
    - Wiadomości CCG (typ 'B') są dodatkowo zapisywane do RabbitMQ

3. **Obsługa Rewind**:
    - Gdy klient wysyła żądanie rewind (typ 'R'), proxy:
        - NIE przekazuje żądania do serwera RC
        - Pobiera wiadomości CCG z lokalnego cache'u
        - Wysyła wiadomości CCG do klienta
        - Wysyła wiadomość "rewind complete" (typ 'r')

## Monitoring

Aplikacja loguje następujące informacje:
- Status połączeń (RC server, klienci)
- Statystyki wiadomości CCG
- Błędy i ostrzeżenia
- Informacje o wydajności

### Logi ważne:
```
[INFO] Starting RC_Proxy_WATS service...
[INFO] RabbitMQ initialized successfully
[INFO] CCG message store initialized successfully
[INFO] Connected to RC server successfully
[INFO] Sent rewind request to RC server, waiting for historical data...
[INFO] Initialization progress: 500 historical CCG messages processed
[INFO] Initialization progress: 1000 historical CCG messages processed
[INFO] Received rewind complete message from RC server
[INFO] RC server initialization completed - loaded 1234 historical CCG messages
[INFO] Proxy is now ready to serve clients with 1234 total CCG messages in store
[INFO] RC_Proxy_WATS service started successfully and ready for client connections
[INFO] Processing rewind request from client {ClientId}
[INFO] Sending {Count} CCG messages to client {ClientId} for rewind
```

## Zarządzanie RabbitMQ

### Management UI
RabbitMQ Management dostępne pod: http://localhost:15672
- Login: guest/guest
- Możesz monitorować kolejki, exchanges i wiadomości

### Czyszczenie kolejki
```bash
# Ręczne czyszczenie kolejki
rabbitmqctl purge_queue ccg_messages
```

## Rozwiązywanie problemów

### Proxy nie łączy się z RC
- Sprawdź czy serwer RC jest uruchomiony
- Zweryfikuj adres i port w konfiguracji
- Sprawdź logi pod kątem błędów sieciowych

### Klient nie może się połączyć z proxy
- Sprawdź czy proxy nasłuchuje na właściwym porcie
- Zweryfikuj ustawienia firewall
- Sprawdź konfigurację `ProxyBindAddress`

### RC GUI nie otrzymuje wiadomości CCG
1. **Sprawdź logi proxy** - powinny pokazywać:
   ```
   [DEBUG] Received CCG message from RC. Session: XXX, Sequence: YYY, Blocks: ZZZ
   [DEBUG] CCG message block types: [B, P, C]  // Przykładowe typy bloków
   [DEBUG] Stored CCG message. Sequence: YYY, Total stored: ZZZ
   ```

2. **Sprawdź rewind operation**:
   ```
   [INFO] Processing rewind request from client {ClientId}
   [INFO] Sending N CCG messages to client {ClientId} for rewind (from sequence 0)
   [DEBUG] Rewind sequence range: 1-1000
   [DEBUG] Sent 100/1000 rewind messages to client {ClientId}
   [INFO] Completed rewind response for client {ClientId}. Sent 1000 messages
   ```

3. **Włącz szczegółowe logowanie**:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "RC_Proxy_WATS": "Trace"
       }
     }
   }
   ```

4. **Sprawdź RabbitMQ Management UI** (http://localhost:15672):
    - Czy kolejka `ccg_messages` zawiera wiadomości?
    - Czy wiadomości są publikowane?
    - Czy nie ma błędów w kolejce?

### RabbitMQ problemy
- Sprawdź czy RabbitMQ jest uruchomiony: `sudo systemctl status rabbitmq-server`
- Zweryfikuj dane logowania
- Sprawdź logi RabbitMQ: `/var/log/rabbitmq/`

### Wysokie zużycie pamięci
- Dostosuj `MaxMessagesInQueue` w konfiguracji
- Zmniejsz `MessageTtlHours`
- Zmniejsz cache w kodzie (`_maxCacheSize`)

## Diagnostyka CCG Messages

### Format wiadomości CCG
RC GUI oczekuje wiadomości w następującym formacie:
```
RC Header (16 bajtów)
├── Session (10 bajtów)
├── Sequence Number (4 bajty)
└── Block Count (2 bajty)

RC Message Blocks:
├── Block 1: Type 'B' (CCG data)
│   ├── Length (2 bajty)
│   └── Payload: 'B' + length + CCG binary data
├── Block 2: Type 'P' (Position data) - opcjonalnie
└── Block N: Type 'X' (inne typy) - opcjonalnie
```

### Testowanie połączenia
Możesz przetestować czy proxy poprawnie przekazuje wiadomości:

1. **Uruchom proxy z debug logging**
2. **Użyj test script**:
   ```bash
   # Sprawdź czy proxy jest gotowy
   dotnet run --project Tools/ProxyTester -- 127.0.0.1 19084
   
   # Czekaj aż proxy będzie gotowy (przydatne w skryptach)
   dotnet run --project Tools/ProxyTester -- --wait-for-ready 127.0.0.1 19084
   ```
3. **Połącz RC GUI z proxy** (port 19084)
4. **Sprawdź logi proxy** - powinny pokazywać:
    - Połączenia klientów
    - Wiadomości od RC serwera
    - Przekazywanie wiadomości do klientów
    - Obsługę żądań rewind

### Typowe problemy

1. **Proxy nie kończy inicjalizacji**:
    - Sprawdź czy RC serwer odpowiada na żądanie rewind
    - Sprawdź logi: powinien być "Sent rewind request" i "Received rewind complete message"
    - Sprawdź czy nie ma timeout (domyślnie 5 minut)

2. **RC GUI pokazuje "Pobieranie wiadomości CCG..." ale nic się nie dzieje**:
    - Sprawdź czy proxy zakończył inicjalizację (powinien pokazać "ready for client connections")
    - Sprawdź czy proxy jest połączony z RC serwerem
    - Sprawdź czy RC serwer wysyła wiadomości CCG
    - Sprawdź logi proxy pod kątem błędów

3. **Rewind nie działa**:
    - Sprawdź czy w RabbitMQ są zapisane wiadomości CCG
    - Sprawdź logi proxy podczas obsługi rewind request
    - Sprawdź czy klient otrzymuje wiadomość "rewind complete"

4. **Proxy używa za dużo pamięci**:
    - Zmniejsz `_maxCacheSize` w `CcgMessageStore`
    - Zmniejsz `MaxMessagesInQueue` w konfiguracji RabbitMQ
    - Zmniejsz `MessageTtlHours`

5. **Długi czas inicjalizacji**:
    - Zależy od liczby historycznych wiadomości CCG na RC serwerze
    - Monitoruj logi "Initialization progress" co 100 wiadomości
    - Domyślny timeout to 5 minut - można zwiększyć w kodzie `InitializeWithRewindAsync()`

## Wydajność

- **Cache**: Przechowuje ostatnie 10,000 wiadomości CCG w pamięci
- **RabbitMQ**: Domyślnie maksymalnie 50,000 wiadomości z TTL 24h
- **Klienci**: Obsługuje do 100 jednoczesnych połączeń

## Bezpieczeństwo

- Aplikacja nie implementuje autoryzacji - powinna być uruchomiona w bezpiecznej sieci
- RabbitMQ używa domyślnych danych logowania - zmień je w środowisku produkcyjnym
- Rozważ użycie TLS dla połączeń w środowisku produkcyjnym

## Rozwój

Struktura projektu:
```
RC_Proxy_WATS/
├── Program.cs                    # Entry point
├── Configuration/               # Klasy konfiguracji
├── Models/                     # Modele danych (RC protokół)
├── Services/                   # Logika biznesowa
│   ├── ProxyService.cs         # Główny serwis
│   ├── RcConnectionService.cs  # Połączenie z RC
│   ├── ClientConnectionManager.cs # Zarządzanie klientami
│   ├── RabbitMqService.cs      # Integracja z RabbitMQ
│   └── CcgMessageStore.cs      # Cache wiadomości
└── appsettings.json            # Konfiguracja
```

## Licencja

[Dodaj odpowiednią licencję]
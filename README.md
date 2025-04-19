  # `IPK25-CHAT`
  
 ## Dokumentácia k 2. úlohe do IPK 2024/2025
 
**Autor:** Jakub Fiľo - xfiloja00

---
###  1. Teória k Projektu

 * TCP (Transmission Control Protocol)
Je to protokol transportnej vrstvy, ktorý umožnuje spolahlive pripojenie k cielovím zariadeniam. Ako prvé nadviaže spojenie pomocou **three-way handshake** kde sa overí pripojenie a až potom posiela dáta. Dáta mozu byt rozdelene do segmentov kvôli velkosti posielanych dat. Protokol TCP vykonova kontrolu aby zarucil ze sa packety nestratia, ku kazdemu packetu pridarí poradové cislo, ktore hovori prijemcovi ake packety mu prichadzaju alebo sa stratili, ak sa packet strati, to znamena ze odosielatel nedostane **ACK** (**acknowledgement**)- správu tak packet pošle znova. Prijemca tiez podla poradovych čisiel vie zostavit data podla poradia.
 * UDP (User Datagram Protocol)
Protokol transportnej vrstvy, označovany ako nespolahlivý, nemá zaruku dorucenia packetov. Pouzivatelia musia rátať s možnými stratami dát pri prenose, nespravnim poradim prichadzajucich packetov alebo prijem moznych duplikátov.
![Alternatívny text obrázka](img/TcpVsUdp.jpg)
 

### 2. Implementácia

#### **Asynchrónne Programovanie**
V nasom projekte vyuzivame asynchronne funkcie aby aplikacia reagovala na vstup od uzivatela aj popri spracovavani správ alebo inej funkcionality. Vyuzívame `async` na oznacenie funkcii, ktoré sa budu vykonávať v tom istom čase na inom vlákne systemu. `await` na asynchronne čakanie procesu/funkcie bez blokovanie ostatnych procesov. Spolu a so spomenutymi funkciami vyuzivame ulohy `Task`, je to objekt, ktory reprezentuje danu operaciu, ktora sa moze aktualne prebiehat a dokonči sa v buducnosti. Tymto sposobom vieme zistit či sa operacia vykonala uspesne alebo s chybou.

V našom projekte máme funkciu `StartClientAsync,` ktorá spustí dve klucove ulohy, ktoré bežia sučasne.  Pre UDP variantu sú to`ReceiveMessagesUdpAsync` a `HandleUserInputUdpAsync.`

 - `ReceiveMessagesUdpAsync`
 Tato uloha neustale caka na prichod UDP datagramov pomocou uz zmieneneho klucoveho slova `await _socket.ReceiveFromAsync(...).` Počas čakania neblokuje ostatne casti programu. Funkcia sa akoby prebudi iba vtedy ked prídu dáta a žacne ich spracovavat (parsovanie atď.)
 - `HandleUserInputUdpAsync`
 Úloha caka asynchronne na vstup od uzivatela z konzoly pomocou `await Task.Run(() => Console.ReadLine(), ...).` Po zadani vstupu uzivatelom sa zavola nasledujuca funkcia na spracovanie obsahu vstupu `ProcessParsedCommandAsync(..).`

#### **Synchronizácia dát**
Nas program vyuziva viacero asynchronnych uloh, ktore bezia sucasne tak by nastal problem keby pouzivame klasicke premenne, jeden proces by chcel zistit hodnotu zatial co druhy by ju chcel zmenit, to moze sposobit poskodenie premmenej alebo pádu programu.
Tento problem v nasom programe riesime pomocoou specialnych datovych typov, ktore su specialne upravene aby fungovali v asynchronnych programoch.  Pouzivame:

 - `ConcurrentDictionary<TKey, TValue>`
 je špeciálny typ slovníka navrhnutý priamo pre situácie, kde k nemu    potrebuje pristupovať viacero úloh (vlákien) naraz. Funguje tak ze si    data interne rozdeli na mensie segmenty a zamyka iba ten segment, s    ktorym prave pracuje.
 
 - `ConcurrentDictionary<ushort, TaskCompletionSource<bool>>   
   _pendingConfirms`
Ukladá informacie o spravach, ktore nas klient dostal **`AUTH`**, **`JOIN`** ... , na ktorych program čaka na  potvrdenie **`CONFIRM`** od servera.
Kluč  **(ushort):**  MessageID správy, ktorú sme my odoslali.
Hodnota **(TaskCompletionSource<bool>):** Objekt, ktorý reprezentuje čakanie. Jeho úloha `Task` je to, na čo čaká metóda `SendReliableUdpMessageAsync` pomocou `await`. Keď prijímacia metoda`ReceiveMessagesUdpAsync` dostane `CONFIRM` od servera so správnym RefMessageId (ktoré zodpovedá nášmu MessageID), nájde tento `TaskCompletionSource` v slovníku a zavolá `tcs.TrySetResult(true)`, čím signalizuje čakajúcej úlohe, že potvrdenie prišlo.
 
 
 - `ConcurrentDictionary<ushort,TaskCompletionSource<Parsed...>>
   _pendingReplies`
   Ukladá informácie o našich odoslaných správach typu **`AUTH`** a **`JOIN`**, pre ktoré sme už dostali **`CONFIRM`**, ale ešte čakáme na funkčnú odpoveď (**`REPLY`**)  od servera.
   Kľúč **(ushort):**  MessageID našej pôvodnej AUTH alebo JOIN správy.
Hodnota **(TaskCompletionSource<ParsedServerMessage>):** Objekt reprezentujúci čakanie. Metóda `SendRequestAndWaitForReplyAsync` naň čaká pomocou `await tcs.Task`. Keď prijímacia metóda dostane a spracuje **`REPLY`** správu, nájde podľa `RefMessageId` v **`REPLY`** správe tento `TaskCompletionSource` a zavolá `tcs.TrySetResult(parsedReply)`, čím odovzdá spracovanú odpoveď čakajúcej úlohe.


 - `ConcurrentDictionary<ushort, byte> _processedIncomingMessageIds`
Ukladá `MessageID` správ, ktoré prišli od servera (**`MSG`**, **`REPLY`**, **`ERR`**, **`BYE`**) a ktoré sme už úspešne spracovali, aby sme zabránili opätovnému spracovaniu duplikátov.
Kľúč **(ushort):**  **`MessageID`** správy prijatej od servera.
Hodnota **(byte):** Len placeholder. Používame ju len preto, že **`ConcurrentDictionary`** potrebuje nejakú hodnotu. Používame byte, lebo zaberá najmenej miesta v pamäti.

#### **Signalizácia ukončenia asynchronnych operacií**
Pri asynchrónnych operáciách potrebujeme spôsob, ako ich predčasne ukončiť, napr. ak používateľ stlačí Ctrl+D. Na toto využivame mechanizmus Cancellation Token. Vytvoríme objekt **`CancellationTokenSource`**, ktorý vie signalizovať metódu na zrušenie. Jeho Token potom odovzdáme asynchrónnym metódam. Tieto metódy môžu buď pravidelne kontrolovať, či bola požiadavka na zrušenie vydaná, alebo automaticky vyhodia výnimku **`OperationCanceledException`**, keď je zrušenie signalizované.
V našom projekte ho využívame takto:
1.  **Vytvorenie Tokenu:** Na začiatku vytvoríme `_cts = new CancellationTokenSource()`.
    
2.  **Odovzdanie Tokenu:**  `_cts.Token` odovzdáme hlavným asynchrónnym metódam `ReceiveMessagesUdpAsync` a `HandleUserInputUdpAsync`. Token sa tiež využivame ďalej v `Task.Delay` v rámci timeoutu, alebo v metóde`SendReliableUdpMessageAsync`.
    
3.  **Signalizácia:** Keď chceme program ukončiť (napr. v handler pre Ctrl+C alebo keď prijme **`BYE`**/**`ERR`** od servera), zavoláme `_cts.Cancel()`.
    
4.  **Reakcia:**
    
    -   Cykly while `(!cancellationToken.IsCancellationRequested)` v `ReceiveMessagesUdpAsync` a `HandleUserInputUdpAsync` sa ukončia.
    
    -   Volania `await` metód, ktoré podporujú `CancellationToken` (ako `_socket.ReceiveFromAsync` alebo `Task.Delay`), vyhodia `OperationCanceledException`, ktorú môžeme zachytiť a vykonať čistenie.
        
    -   V metóde `CancelAllPendingOperations` prejdeme cez `_pendingConfirms` a `_pendingReplies` a zavoláme `TrySetCanceled()` na všetkých čakajúcich `TaskCompletionSource`, aby sme explicitne zrušili úlohy čakajúce na **`CONFIRM`** alebo **`REPLY`**.
        
5.  **Čistenie:** Po signalizácii zrušenia a dobehnutí úloh sa priamo v `InitiateShutdownAsync` vykoná čistenie zdrojov (`OwnDispose`).

#### Časovač 
Pri komunikácii, kde odpoveď nie je zaručená ako je protokol `IPK25-CHAT`, potrebujeme mechanizmus, ktorý nám povie, kedy už nemá zmysel ďalej čakať. Časovač spustíme po odoslaní správy a ak odpoveď (napr. **`CONFIRM`**) nepríde do stanoveného limitu, časovač vyprší a my môžeme vykonať nasledujucu akciu.
V našom projekte namiesto klasických Timer tried využívame schopnosti async/await:

-   **Čakanie na Funkčnú Odpoveď (`SendRequestAndWaitForReplyAsync`):**
    
    -   Podobne, po úspešnom prijatí CONFIRM pre AUTH alebo JOIN, vytvoríme `TaskCompletionSource<ParsedServerMessage>` (`replyTcs`) a pridáme ho do `_pendingReplies`.
        
    -   Použijeme `await Task.WhenAny(replyTcs.Task, Task.Delay(ReplyTimeoutMilliseconds, linkedCts.Token))` na čakanie buď na príchod REPLY správy (signalizované cez `replyTcs.TrySetResult(...)`) alebo na vypršanie dlhšieho timeoutu pre **`REPLY`**.
        
    -   Podľa toho, ktorá úloha skončí prvá, program buď spracuje prijatú **`REPLY`**, alebo ohlási chybu timeoutu.

#### **Štruktára programu**






-   V tomto případě je diagram prakticky nezbytný – z kvalitního diagramu čtenář často snáze pochopí úroveň kvality implementace než z delšího textového popisu.




### Bibliografia

Cisco Networking Academy. IPv4 vs IPv6. Online. Available from: https://www.networkacademy.io/ccna/ipv6/ipv4-vs-ipv6 [Accessed 27 March 2025].

inc0x0. TCP/IP Packets Introduction - Part 3: Manually Create and Send Raw TCP/IP Packets. Online. Available from: https://inc0x0.com/tcp-ip-packets-introduction/tcp-ip-packets-3-manually-create-and-send-raw-tcp-ip-packets/ [Accessed 27 March 2025].

Wikipedia contributors. Transmission Control Protocol. Online. Available from: https://cs.wikipedia.org/wiki/Transmission_Control_Protocol [Accessed 27 March 2025].

Wikipedia contributors. User Datagram Protocol. Online. Available from: https://cs.wikipedia.org/wiki/User_Datagram_Protocol [Accessed 27 March 2025].

Microsoft. .NET API Browser. Online. Available from: https://learn.microsoft.com/en-us/dotnet/api/system.net?view=net-9.0 [Accessed 27 March 2025].

Nmap Project. scanme.nmap.org. Online. Available from: http://scanme.nmap.org/ [Accessed 27 March 2025].


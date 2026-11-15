---
name: pdf
description: Создать PDF из Word, объединить PDF, извлечь текст или распознать сканы и изображения.
---
Вызови `process-pdf.ps1` через run_plugin, передав `Mode`, `Source` и `Target`.

- `convert`: DOCX в PDF, требуется Microsoft Word. Чтобы создать PDF с нуля, сначала создай DOCX плагином word.
- `merge`: папка с PDF в один PDF. Нужен qpdf в PATH. Файлы идут по имени. Если нужен другой порядок или подмножество файлов, подготовь папку с копиями и числовыми префиксами.
- `extract`: PDF с текстовым слоем в TXT. Нужен `pdftotext` из Poppler в PATH.
- `ocr`: PNG, JPEG, TIFF, BMP или PDF в TXT. Нужен Tesseract с языковыми данными. Для PDF также нужен `pdftoppm` из Poppler. `Language` по умолчанию `rus+eng`. Для PDF можно ограничить страницы через `FirstPage` и `LastPage`.

Если внешний инструмент недоступен, сообщи, что именно нужно установить. Скрипт ничего не скачивает. Для замены существующего результата используй `Overwrite=yes` с подтверждением.

Get-WinEvent -ProviderName 'Application Error' -MaxEvents 20 | Where-Object { $_.Message -like '*WinAssistant*' } | Format-List TimeCreated, Message

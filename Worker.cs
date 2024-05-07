using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime;
using System.Threading;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TestWorkerService
{
    public sealed class WindowsBackgroundService: BackgroundService
    {
        private readonly AppSettings _settings;
        private readonly ILogger<WindowsBackgroundService> _logger;
        private readonly BotService _botService;
        private  ITelegramBotClient? _botClient;
        private ReceiverOptions? _receiverOptions;

        public WindowsBackgroundService(IOptions<AppSettings> settings, ILogger<WindowsBackgroundService> logger, BotService botService) 
        {
            _settings = settings.Value;
            _logger = logger;
            _botService = botService;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
               
                await _botService.UpdateTableAsync();
                if (!_botService.isDataReceived)
                {
                    Environment.Exit(1);
                }
                _logger.LogCritical("Service started");
                _botClient = new TelegramBotClient(_settings.TelegramBotToken);
                _receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = new[]
                    {
                            UpdateType.Message,
                            UpdateType.CallbackQuery,
                    },
                    ThrowPendingUpdates = true,
                };
                _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, _receiverOptions, stoppingToken);
                int timer = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                    if (++timer == 60*60) 
                    {
                        await _botService.UpdateTableAsync();
                        _logger.LogWarning("The data is updated");
                        timer = 0;
                    }

                }
            }
            catch (OperationCanceledException)
            {
                // When the stopping token is canceled, for example, a call made from services.msc,
                // we shouldn't exit with a non-zero exit code. In other words, this is expected...
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Message}", ex.Message);
                Environment.Exit(1);
            }
        }
        private async Task BotMessageHandler(Telegram.Bot.Types.Message message)
        {
            var user = message?.From;
            var chat = message?.Chat;
            if (_botService.IsIdAllowedDb(user.Id)) 
            {
                if (message.Type == MessageType.Text)
                {
                    _logger.LogWarning($"Authorized id: {message.From.Id} Message:  {message.Text}");

                   await BotStartHandler(user.Id);
                }
            }
            else
            {
                if (message.Type == MessageType.Contact)
                {
                    _logger.LogWarning($"Contact:  {message.Contact.FirstName} {message.Contact.LastName} {message.Contact.PhoneNumber}");
                    if (_botService.IsPhoneNumberAllowed(message.Contact.PhoneNumber, user.Id))
                    {
                        await _botClient.SendTextMessageAsync(
                                chatId: chat.Id,
                                text: $"Здравствуйте, {message.Contact.FirstName}",
                                replyMarkup: new ReplyKeyboardRemove()); 
                        await BotStartHandler(user.Id);
                    }
                }
                else
                {
                    _logger.LogWarning($"Not authorized id: {message.From.Id} Message:  {message.Text}");
                    ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
                    {
                        KeyboardButton.WithRequestContact("Отправить контактные данные"),
                    });
                    replyKeyboardMarkup.ResizeKeyboard = true;
                    await _botClient.SendTextMessageAsync(chatId: chat.Id,
                        text: _settings.GreetingText,
                        replyMarkup: replyKeyboardMarkup);
                }
                
            }
            
        }
        private async Task BotCallBackHandler(Telegram.Bot.Types.CallbackQuery callbackQuery)
        {
            var user = callbackQuery?.From;
            var chat = callbackQuery.Message.Chat;
            _logger.LogWarning($"Callback: User: {user.Id} callbackQuery: {callbackQuery.Data}");
            try
            {
                var deleteTask = _botClient.DeleteMessageAsync(chat.Id, callbackQuery.Message.MessageId);
                var replayTask = BotCallBackHandler(chat.Id, callbackQuery.Data);
                await Task.WhenAll(deleteTask, replayTask);
            }
            catch (Exception) { }
        }
        private async Task BotStartHandler(long chatId)
        {
            

            await _botClient.SendTextMessageAsync(chatId,
                "Это бот базы знаний. Выберите раздел",
                replyMarkup: _botService.createStandardMarkup());

        }
        private async Task BotCallBackHandler(long chatId, string callBackData)
        {
            if (!callBackData.StartsWith(_settings.CallBackDataPrefix))
            {
                await _botClient.SendTextMessageAsync(chatId,
                "Сотрите историю и начните заново");
                return;
            }
            await _botClient.SendTextMessageAsync(chatId,
                "Нажмите на ссылку для просмотра видео",
                replyMarkup: _botService.createStandardMarkup(callBackData));
        }
 
        async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                {
                    await BotMessageHandler(update.Message);
                    break;
                }
                case UpdateType.CallbackQuery:
                {
                    await BotCallBackHandler(update.CallbackQuery);
                    break;
                }
            }
            
        }

        async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException are => $"Telegram API Error:\n[{are.ErrorCode}]\n{are.Message}",
                _ => exception.ToString()
            };
            _logger.LogError(errorMessage);
            await Task.CompletedTask;
        }
    }
}


﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portalum.Zvt.Helpers;
using Portalum.Zvt.Models;
using Portalum.Zvt.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Portalum.Zvt
{
    /// <summary>
    /// ZVT Client
    /// </summary>
    public class ZvtClient : IDisposable
    {
        /*
         * The implementation of this client is based on the following documents
         * ZVT Revision 13.09 of terminalhersteller.de (2020-11-20)
         * - https://www.terminalhersteller.de/downloads/PA00P016_04_en.pdf
         * - https://www.terminalhersteller.de/downloads/PA00P015_13.09_final_en.pdf
        */

        private readonly ILogger<ZvtClient> _logger;
        private readonly byte[] _passwordData;

        private readonly ZvtCommunication _zvtCommunication;
        private IReceiveHandler _receiveHandler;
        private readonly TimeSpan _commandCompletionTimeout;

        #region Events

        /// <summary>
        /// Receive detailed Information about a transaction
        /// </summary>
        public event Action<StatusInformation> StatusInformationReceived;

        /// <summary>
        /// Receive the current transaction status for example 'waiting for card'
        /// </summary>
        public event Action<string> IntermediateStatusInformationReceived;

        /// <summary>
        /// Receive single lines of a receipt
        /// </summary>
        public event Action<PrintLineInfo> LineReceived;

        /// <summary>
        /// Receive a full receipt at once
        /// </summary>
        public event Action<ReceiptInfo> ReceiptReceived;

        #endregion

        /// <summary>
        /// ZvtClient
        /// </summary>
        /// <param name="deviceCommunication"></param>
        /// <param name="logger"></param>
        /// <param name="clientConfig">ZVT Configuration</param>
        /// <param name="receiveHandler">Inject own receive handler</param>
        public ZvtClient(
            IDeviceCommunication deviceCommunication,
            ILogger<ZvtClient> logger = default,
            ZvtClientConfig clientConfig = default,
            IReceiveHandler receiveHandler = default)
        {
            if (logger == null)
            {
                logger = new NullLogger<ZvtClient>();
            }
            this._logger = logger;

            if (clientConfig == default)
            {
                clientConfig = new ZvtClientConfig();
            }

            this._commandCompletionTimeout = clientConfig.CommandCompletionTimeout;

            this._passwordData = NumberHelper.IntToBcd(clientConfig.Password);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            #region ReceiveHandler

            if (receiveHandler == default)
            {
                this.InitializeReceiveHandler(clientConfig.Language, this.GetEncoding(clientConfig.Encoding));
            }
            else
            {
                this._receiveHandler = receiveHandler;
                this.RegisterReceiveHandlerEvents();
            }

            #endregion

            this._zvtCommunication = new ZvtCommunication(logger, deviceCommunication);
            this._zvtCommunication.DataReceived += this.DataReceived;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._zvtCommunication.DataReceived -= this.DataReceived;
                this._zvtCommunication.Dispose();

                this.UnregisterReceiveHandlerEvents();
            }
        }

        private Encoding GetEncoding(ZvtEncoding zvtEncoding)
        {
            switch (zvtEncoding)
            {
                case ZvtEncoding.UTF8:
                    return Encoding.UTF8;
                case ZvtEncoding.ISO_8859_1:
                    return Encoding.GetEncoding("iso-8859-1");
                case ZvtEncoding.ISO_8859_2:
                    return Encoding.GetEncoding("iso-8859-2");
                case ZvtEncoding.ISO_8859_15:
                    return Encoding.GetEncoding("iso-8859-15");
                case ZvtEncoding.CodePage437:
                default:
                    return Encoding.GetEncoding(437);
            }
        }

        private void InitializeReceiveHandler(
            Language language,
            Encoding encoding)
        {
            IErrorMessageRepository errorMessageRepository = this.GetErrorMessageRepository(language);
            IIntermediateStatusRepository intermediateStatusRepository = this.GetIntermediateStatusRepository(language);

            this._receiveHandler = new ReceiveHandler(this._logger, encoding, errorMessageRepository, intermediateStatusRepository);
            this.RegisterReceiveHandlerEvents();
        }

        private void RegisterReceiveHandlerEvents()
        {
            this._receiveHandler.IntermediateStatusInformationReceived += this.ProcessIntermediateStatusInformationReceived;
            this._receiveHandler.StatusInformationReceived += this.ProcessStatusInformationReceived;
            this._receiveHandler.LineReceived += this.ProcessLineReceived;
            this._receiveHandler.ReceiptReceived += this.ProcessReceiptReceived;
        }

        private void UnregisterReceiveHandlerEvents()
        {
            this._receiveHandler.IntermediateStatusInformationReceived -= this.ProcessIntermediateStatusInformationReceived;
            this._receiveHandler.StatusInformationReceived -= this.ProcessStatusInformationReceived;
            this._receiveHandler.LineReceived -= this.ProcessLineReceived;
            this._receiveHandler.ReceiptReceived -= this.ProcessReceiptReceived;
        }

        private IErrorMessageRepository GetErrorMessageRepository(Language language)
        {
            //No German translation available
            return new EnglishErrorMessageRepository();
        }

        private IIntermediateStatusRepository GetIntermediateStatusRepository(Language language)
        {
            if (language == Language.German)
            {
                return new GermanIntermediateStatusRepository();
            }
            
            return new EnglishIntermediateStatusRepository();
        }

        private void ProcessIntermediateStatusInformationReceived(string message)
        {
            this.IntermediateStatusInformationReceived?.Invoke(message);
        }

        private void ProcessStatusInformationReceived(StatusInformation statusInformation)
        {
            this.StatusInformationReceived?.Invoke(statusInformation);
        }

        private void ProcessLineReceived(PrintLineInfo printLineInfo)
        {
            this.LineReceived?.Invoke(printLineInfo);
        }

        private void ProcessReceiptReceived(ReceiptInfo receiptInfo)
        {
            this.ReceiptReceived?.Invoke(receiptInfo);
        }

        private bool DataReceived(byte[] data)
        {
            var processDataState = this._receiveHandler.ProcessData(data);
            switch (processDataState)
            {
                case ProcessDataState.CannotProcess:
                case ProcessDataState.ParseFailure:
                    this._logger.LogError($"{nameof(DataReceived)} - Unprocessable data received {BitConverter.ToString(data)}");
                    return false;
                case ProcessDataState.WaitForMoreData:
                    return false;
                case ProcessDataState.Processed:
                    return true;
            }

            this._logger.LogCritical($"{nameof(DataReceived)} - Invalid state {processDataState}");
            return false;
        }

        /// <summary>
        /// SendCommandAsync
        /// </summary>
        /// <param name="commandData">The data of the command</param>
        /// <param name="endAfterAcknowledge">After receive an acknowledge the command is successful</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<CommandResponse> SendCommandAsync(
            byte[] commandData,
            bool endAfterAcknowledge = false,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCancellationTokenSource = new CancellationTokenSource(this._commandCompletionTimeout);
            using var dataReceivcedCancellationTokenSource = new CancellationTokenSource();
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dataReceivcedCancellationTokenSource.Token, timeoutCancellationTokenSource.Token);

            var commandResponse = new CommandResponse
            {
                State = CommandResponseState.Unknown
            };

            void completionReceived()
            {
                commandResponse.State = CommandResponseState.Successful;

                dataReceivcedCancellationTokenSource.Cancel();
            }

            void abortReceived(string errorMessage)
            {
                commandResponse.State = CommandResponseState.Abort;
                commandResponse.ErrorMessage = errorMessage;

                dataReceivcedCancellationTokenSource.Cancel();
            }

            void intermediateStatusInformationReceived(string status)
            {
                timeoutCancellationTokenSource.CancelAfter(this._commandCompletionTimeout);
            }

            void statusInformationReceived(StatusInformation statusInformation)
            {
                timeoutCancellationTokenSource.CancelAfter(this._commandCompletionTimeout);
            }

            try
            {
                this._receiveHandler.CompletionReceived += completionReceived;
                this._receiveHandler.AbortReceived += abortReceived;
                this._receiveHandler.IntermediateStatusInformationReceived += intermediateStatusInformationReceived;
                this._receiveHandler.StatusInformationReceived += statusInformationReceived;

                this._logger.LogDebug($"{nameof(SendCommandAsync)} - Send command to PT");

                var sendCommandResult = await this._zvtCommunication.SendCommandAsync(commandData, cancellationToken: cancellationToken);
                if (sendCommandResult == SendCommandResult.NotSupported)
                {
                    this._logger.LogError($"{nameof(SendCommandAsync)} - NotSupported");
                    commandResponse.State = CommandResponseState.NotSupported;
                    return commandResponse;
                }
                if (sendCommandResult != SendCommandResult.PositiveCompletionReceived)
                {
                    this._logger.LogError($"{nameof(SendCommandAsync)} - Failure on send command");
                    commandResponse.State = CommandResponseState.Error;
                    commandResponse.ErrorMessage = sendCommandResult.ToString();

                    return commandResponse;
                }

                if (endAfterAcknowledge)
                {
                    commandResponse.State = CommandResponseState.Successful;
                    return commandResponse;
                }

                // There is no infinite timeout here, the timeout comes via the `timeoutCancellationTokenSource`
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellationTokenSource.Token).ContinueWith(task =>
                {
                    if (timeoutCancellationTokenSource.IsCancellationRequested)
                    {
                        commandResponse.State = CommandResponseState.Timeout;
                        this._logger.LogError($"{nameof(SendCommandAsync)} - No result received in the specified timeout {this._commandCompletionTimeout.TotalMilliseconds}ms");
                    }
                });
            }
            finally
            {
                this._receiveHandler.AbortReceived -= abortReceived;
                this._receiveHandler.CompletionReceived -= completionReceived;
                this._receiveHandler.IntermediateStatusInformationReceived -= intermediateStatusInformationReceived;
                this._receiveHandler.StatusInformationReceived -= statusInformationReceived;
            }

            return commandResponse;
        }

        private byte[] CreatePackage(
            byte[] controlField,
            IEnumerable<byte> packageData)
        {
            var package = new List<byte>();
            package.AddRange(controlField);
            package.Add((byte)packageData.Count());
            package.AddRange(packageData);
            return package.ToArray();
        }

        /// <summary>
        /// Registration (06 00)
        /// Using the command Registration the ECR can set up different configurations on the PT and also control the current status of the PT.
        /// </summary>
        /// <param name="registrationConfig"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> RegistrationAsync(
            RegistrationConfig registrationConfig,
            CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(RegistrationAsync)} - Execute");

            _ = registrationConfig ?? throw new ArgumentNullException(nameof(registrationConfig));

            var configByte = registrationConfig.GetConfigByte();
            var serviceByte = registrationConfig.GetServiceByte();

            var package = new List<byte>();
            package.AddRange(this._passwordData);
            package.Add(configByte);

            //Currency Code (CC)
            //ISO4217 (https://en.wikipedia.org/wiki/ISO_4217)
            var currencyNumericCodeData = NumberHelper.IntToBcd(978, 2); //978 = Euro
            package.AddRange(currencyNumericCodeData);

            //Service byte
            package.Add(0x03); //Service byte indicator
            package.Add(serviceByte);

            if (registrationConfig.ActivateTlvSupport)
            {
                //Add empty TLV Container
                //package.Add(0x06); //TLV
                //package.Add(0x00); //TLV-Length

                //Add TLV Container permit 06D3 (Card complete)
                package.Add(0x06); //TLV Indicator
                package.Add(0x06); //TLV Legnth

                package.Add(0x26); //List of permitted ZVT-Commands
                package.Add(0x04); //length
                package.Add(0x0A); //ZVT-command
                package.Add(0x02); //length
                package.Add(0x06); //06 first hex of print text block
                package.Add(0xD3); //D3 second hex of print text block

                //TLV TAG
                //10 - Number of columns and number of lines of the merchant-display
                //11 - Number of columns and number of lines of the customer-display
                //12 - Number of characters per line of the printer
                //14 - ISO-Character set
                //1A - Max length the APDU
                //26 - List of permitted ZVT commands
                //27 - List of supported character-sets
                //28 - List of supported languages
                //29 - List of menus which should be displayed over the ECR or on a second customer-display
                //2A - List of menus which the ECR will not display and therefore must be displayed on the PT
                //40 - EMV-configuration-parameter
                //1F04 - Receipt parameter
                //1F05 - Transaction parameter
            }

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x00 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Authorization (06 01)
        /// Payment process and transmits the amount from the ECR to PT.
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> PaymentAsync(
            decimal amount,
            CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(PaymentAsync)} - Execute with amount of:{amount}");

            var package = new List<byte>();
            package.Add(0x04); //Amount prefix
            package.AddRange(NumberHelper.DecimalToBcd(amount));

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x01 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Reversal (06 30)
        /// This command reverses a payment-procedure and transfers the receipt-number of the transaction to be reversed from the ECR to PT.
        /// The result of the reversal-process is sent to the ECR after Completion of the booking-process.
        /// </summary>
        /// <param name="receiptNumber">four-digit number</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> ReversalAsync(
            int receiptNumber,
            CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(ReversalAsync)} - Execute");

            var package = new List<byte>();
            package.AddRange(this._passwordData);
            package.Add(0x87); //ReceiptNumber prefix
            package.AddRange(NumberHelper.IntToBcd(receiptNumber, 2));

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x30 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Refund (06 31)
        /// This command starts a Refund on the PT. The result of the Refund is reported to the ECR after completion of the booking-process.
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> RefundAsync(
            decimal amount,
            CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(RefundAsync)} - Execute");

            var package = new List<byte>();
            package.AddRange(this._passwordData);
            package.Add(0x04); //Amount prefix
            package.AddRange(NumberHelper.DecimalToBcd(amount));

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x31 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// End-of-Day (06 50)
        /// ECR induces the PT to transfer the stored turnover to the host.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> EndOfDayAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(EndOfDayAsync)} - Execute");

            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x50 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Send Turnover Totals (06 10)
        /// With this command the ECR causes the PT to send an overview about the stored transactions.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> SendTurnoverTotalsAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(SendTurnoverTotalsAsync)} - Execute");

            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x10 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Repeat Receipt (06 20)
        /// This command serves to repeat printing of the last stored payment-receipts or End-of-Day-receipt.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> RepeatLastReceiptAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(RepeatLastReceiptAsync)} - Execute");

            var package = new List<byte>();
            package.AddRange(this._passwordData);

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x20 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Log-Off (06 02)
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> LogOffAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(LogOffAsync)} - Execute");

            var package = Array.Empty<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x02 }, package);
            return await this.SendCommandAsync(fullPackage, endAfterAcknowledge: true, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Abort (06 B0)
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> AbortAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(AbortAsync)} - Execute");

            var package = Array.Empty<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0xB0 }, package);
            return await this.SendCommandAsync(fullPackage, endAfterAcknowledge: true, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Diagnosis (06 70)
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> DiagnosisAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(DiagnosisAsync)} - Execute");

            var package = Array.Empty<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x06, 0x70 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Software-Update (08 10)
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> SoftwareUpdateAsync(CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(SoftwareUpdateAsync)} - Execute");

            var package = Array.Empty<byte>();

            var fullPackage = this.CreatePackage(new byte[] { 0x08, 0x10 }, package);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Custom Command, allows to send unimplemented commands
        /// </summary>
        /// <param name="controlFieldData">CCRC and APRC, for example 0x08, 0x01</param>
        /// <param name="packageData"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<CommandResponse> CustomCommandAsync(
            byte[] controlFieldData,
            byte[] packageData = default,
            CancellationToken cancellationToken = default)
        {
            this._logger.LogInformation($"{nameof(CustomCommandAsync)} - Execute");

            if (packageData == null)
            {
                packageData = Array.Empty<byte>();
            }

            var fullPackage = this.CreatePackage(controlFieldData, packageData);
            return await this.SendCommandAsync(fullPackage, cancellationToken: cancellationToken);
        }
    }
}

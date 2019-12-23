﻿using System.Linq;
using AElf.Blockchains.MainChain;
using AElf.Boilerplate.Tester;
using AElf.Contracts.Deployer;
using AElf.Contracts.Genesis;
using AElf.Database;
using AElf.Kernel;
using AElf.Kernel.Consensus;
using AElf.Kernel.Consensus.AEDPoS;
using AElf.Kernel.Infrastructure;
using AElf.Kernel.SmartContract;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract.Parallel;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.Kernel.Token;
using AElf.Kernel.Txn.Application;
using AElf.Modularity;
using AElf.OS;
using AElf.OS.Network.Grpc;
using AElf.OS.Node.Application;
using AElf.OS.Node.Domain;
using AElf.Runtime.CSharp;
using AElf.RuntimeSetup;
using AElf.WebApp.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.AspNetCore;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;

namespace AElf.Boilerplate.MainChain
{
    [DependsOn(
        typeof(KernelAElfModule),
        typeof(AEDPoSAElfModule),
        typeof(TokenKernelAElfModule),
        typeof(OSAElfModule),
        typeof(AbpAspNetCoreModule),
        typeof(CSharpRuntimeAElfModule),
        typeof(GrpcNetworkModule),
        typeof(RuntimeSetupAElfModule),

        //web api module
        typeof(WebWebAppAElfModule),

        typeof(ParallelExecutionModule),
        
        // test contracts by sending txs
        typeof(TesterModule)
    )]
    public class MainChainModule : AElfModule
    {
        public OsBlockchainNodeContext OsBlockchainNodeContext { get; set; }

        public ILogger<MainChainModule> Logger { get; set; }

        public MainChainModule()
        {
            Logger = NullLogger<MainChainModule>.Instance;
        }

        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            var configuration = context.Services.GetConfiguration();
            var hostBuilderContext = context.Services.GetSingletonInstanceOrNull<HostBuilderContext>();

            var newConfig = new ConfigurationBuilder().AddConfiguration(configuration)
                .AddJsonFile("appsettings.MainChain.MainNet.json")
                .SetBasePath(context.Services.GetHostingEnvironment().ContentRootPath)
                .Build();

            hostBuilderContext.Configuration = newConfig;
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var s = context.Services;
            s.AddKeyValueDbContext<BlockchainKeyValueDbContext>(o => o.UseInMemoryDatabase());
            s.AddKeyValueDbContext<StateKeyValueDbContext>(o => o.UseInMemoryDatabase());
            s.TryAddSingleton<ISmartContractAddressNameProvider, ConsensusSmartContractAddressNameProvider>();
            s.TryAddSingleton<ISmartContractAddressNameProvider, ElectionSmartContractAddressNameProvider>();
            s.TryAddSingleton<ISmartContractAddressNameProvider, ProfitSmartContractAddressNameProvider>();
            s.TryAddSingleton<ISmartContractAddressNameProvider, TokenConverterSmartContractAddressNameProvider>();
            s.TryAddSingleton<ISmartContractAddressNameProvider, TokenSmartContractAddressNameProvider>();
            s.TryAddSingleton<ISmartContractAddressNameProvider, VoteSmartContractAddressNameProvider>();

            var configuration = context.Services.GetConfiguration();
            Configure<AElf.OS.EconomicOptions>(configuration.GetSection("Economic"));
            Configure<ChainOptions>(option =>
            {
                option.ChainId =
                    ChainHelper.ConvertBase58ToChainId(context.Services.GetConfiguration()["ChainId"]);
                option.ChainType = context.Services.GetConfiguration().GetValue("ChainType", ChainType.MainChain);
                option.NetType = context.Services.GetConfiguration().GetValue("NetType", NetType.MainNet);
            });

            Configure<HostSmartContractBridgeContextOptions>(options =>
            {
                options.ContextVariables[ContextVariableDictionary.NativeSymbolName] = context.Services
                    .GetConfiguration().GetValue("Economic:Symbol", "ELF");
                options.ContextVariables[ContextVariableDictionary.ResourceTokenSymbolList] = context.Services
                    .GetConfiguration().GetValue("Economic:ResourceTokenSymbolList", "STO,RAM,CPU,NET");
            });

            Configure<ContractOptions>(configuration.GetSection("Contract"));

            context.Services.AddSingleton<ISystemContractProvider, SystemContractProvider>();
            context.Services.AddSingleton(typeof(ContractsDeployer));

            context.Services.AddTransient<IGenesisSmartContractDtoProvider, GenesisSmartContractDtoProvider>();

            context.Services.RemoveAll<ITransactionValidationProvider>();
        }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            var chainOptions = context.ServiceProvider.GetService<IOptionsSnapshot<ChainOptions>>().Value;
            var dto = new OsBlockchainNodeContextStartDto()
            {
                ChainId = chainOptions.ChainId,
                ZeroSmartContract = typeof(BasicContractZero)
            };

            var zeroContractAddress = context.ServiceProvider.GetRequiredService<ISmartContractAddressService>()
                .GetZeroSmartContractAddress();
            var dtoProvider = context.ServiceProvider.GetRequiredService<IGenesisSmartContractDtoProvider>();

            dto.InitializationSmartContracts = dtoProvider.GetGenesisSmartContractDtos(zeroContractAddress).ToList();
            var contractOptions = context.ServiceProvider.GetService<IOptionsSnapshot<ContractOptions>>().Value;
            dto.ContractDeploymentAuthorityRequired = contractOptions.ContractDeploymentAuthorityRequired;

            var osService = context.ServiceProvider.GetService<IOsBlockchainNodeContextService>();
            var that = this;
            AsyncHelper.RunSync(async () => { that.OsBlockchainNodeContext = await osService.StartAsync(dto); });
        }

        public override void OnApplicationShutdown(ApplicationShutdownContext context)
        {
            var osService = context.ServiceProvider.GetService<IOsBlockchainNodeContextService>();
            var that = this;
            AsyncHelper.RunSync(() => osService.StopAsync(that.OsBlockchainNodeContext));
        }
    }
}
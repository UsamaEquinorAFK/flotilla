import { useEffect, useState } from 'react'
import { useIsAuthenticated } from '@azure/msal-react'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from '../../../api/AuthConfig'
import { Autocomplete, Button, TopBar, CircularProgress, Typography, Checkbox } from '@equinor/eds-core-react'
import { IPublicClientApplication } from '@azure/msal-browser'
import styled from 'styled-components'
import { useLanguageContext } from 'components/Contexts/LanguageContext'
import { useInstallationContext } from 'components/Contexts/InstallationContext'
import { BackendAPICaller } from 'api/ApiCaller'
import { EchoPlantInfo } from 'models/EchoMission'
import { Header } from 'components/Header/Header'
import { config } from 'config'

const Centered = styled.div`
    display: flex;
    flex-direction: column;
    align-items: center;
`

function handleLogin(instance: IPublicClientApplication) {
    instance.loginRedirect(loginRequest).catch((e) => {
        console.error(e)
    })
}
const StyledTopBarContent = styled(TopBar.CustomContent)`
    display: grid;
    grid-template-columns: minmax(50px, 265px) auto;
    gap: 0px 3rem;
    align-items: center;
`
const BlockLevelContainer = styled.div`
    & > * {
        display: block;
    }
`
const StyledCheckbox = styled(Checkbox)`
    margin-left: -14px;
`
const RowContainer = styled.div`
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    justify-content: flex-start;
    margin-top: 50px;
`

export const AssetSelectionPage = () => {
    const isAuthenticated = useIsAuthenticated()
    const { instance } = useMsal()
    const { TranslateText } = useLanguageContext()

    useEffect(() => {
        if (!isAuthenticated) {
            handleLogin(instance)
        }
    }, [isAuthenticated, instance])

    return (
        <>
            {isAuthenticated ? (
                <>
                    <Header page={'root'} />
                    <Centered>
                        <RowContainer>
                            <StyledTopBarContent>{InstallationPicker()}</StyledTopBarContent>

                            <Button href={`${config.FRONTEND_BASE_ROUTE}/FrontPage`}>
                                {TranslateText('Confirm installation')}
                            </Button>
                        </RowContainer>
                        {/* TODO! ADD image here*/}
                    </Centered>
                </>
            ) : (
                <Centered>
                    <Typography variant="body_long_bold" color="primary">
                        Authentication
                    </Typography>
                    <CircularProgress size={48} color="primary" />
                </Centered>
            )}
        </>
    )
}

function InstallationPicker() {
    const [allPlantsMap, setAllPlantsMap] = useState<Map<string, string>>(new Map())
    const { installationName, switchInstallation } = useInstallationContext()
    const { TranslateText } = useLanguageContext()
    const [showActivePlants, setShowActivePlants] = useState<boolean>(true)
    const [updateListOfActivePlants, setUpdateListOfActivePlants] = useState<boolean>(false)

    useEffect(() => {
        const plantPromise = showActivePlants ? BackendAPICaller.getActivePlants() : BackendAPICaller.getEchoPlantInfo()
        plantPromise.then(async (response: EchoPlantInfo[]) => {
            const mapping = mapInstallationCodeToName(response)
            setAllPlantsMap(mapping)
        })
    }, [showActivePlants, updateListOfActivePlants])
    const mappedOptions = allPlantsMap ? allPlantsMap : new Map<string, string>()
    return (
        <>
            <BlockLevelContainer>
                <Autocomplete
                    options={Array.from(mappedOptions.keys()).sort()}
                    label=""
                    initialSelectedOptions={[installationName]}
                    placeholder={TranslateText('Select installation')}
                    onOptionsChange={({ selectedItems }) => {
                        const selectedName = selectedItems[0]
                        switchInstallation(selectedName)
                    }}
                    autoWidth={true}
                    onFocus={(e) => {
                        e.preventDefault()
                        setUpdateListOfActivePlants(!updateListOfActivePlants)
                    }}
                />

                <StyledCheckbox
                    label={TranslateText('Show only active installations')}
                    checked={showActivePlants}
                    onChange={(e) => setShowActivePlants(e.target.checked)}
                />
            </BlockLevelContainer>
        </>
    )
}

const mapInstallationCodeToName = (echoPlantInfoArray: EchoPlantInfo[]): Map<string, string> => {
    var mapping = new Map<string, string>()
    echoPlantInfoArray.forEach((echoPlantInfo: EchoPlantInfo) => {
        mapping.set(echoPlantInfo.projectDescription, echoPlantInfo.plantCode)
    })
    return mapping
}